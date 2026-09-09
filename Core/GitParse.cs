using System.Globalization;

namespace CoderMascot.Core;

/// <summary>
/// Turning git's output into the answer to "what have I merged where".
///
/// Kept pure and away from the process spawning for one reason: this is the part
/// that is easy to get quietly wrong. A merge subject parsed to the wrong branch
/// name, or an ancestry check read backwards, produces a screen that looks
/// authoritative and is lying — which is worse for the person relying on it than
/// showing nothing at all. Here it can be tested against real git output on a
/// machine with no git and no Windows.
/// </summary>
public static class GitParse
{
    /// <summary>
    /// Field separator asked of git. A unit separator cannot occur in a ref
    /// name, an author name or a commit subject, so nothing needs escaping and
    /// a branch called "feature/a|b" can't split a row in half.
    /// </summary>
    public const char Sep = '\u001F';

    /// <summary>for-each-ref: refname, sha, unix time, author, subject.</summary>
    /// <summary>
    /// for-each-ref: name, sha, when, who, subject.
    ///
    /// The separator is spelled "%1f" here and "%x1f" in <see cref="LogFormat"/>
    /// below, and the difference is not a typo. `git log` takes a *pretty*
    /// format, where a literal byte is %xNN; `git for-each-ref` takes a *ref*
    /// format, where it is %NN. Give for-each-ref the pretty spelling and it
    /// prints the four characters "%x1f" instead of a separator, every field
    /// runs into the next, and the parser finds no branches at all — which
    /// surfaces as "No branches found in this repository" for every repository
    /// on earth, with git exiting 0 the whole time.
    /// </summary>
    public const string RefFormat =
        "%(refname)%1f%(objectname:short)%1f%(committerdate:unix)%1f%(authorname)%1f%(contents:subject)";

    /// <summary>The same, with ahead/behind against a target appended.</summary>
    public static string RefFormatWithCounts(string targetRef) =>
        RefFormat + "%1f%(ahead-behind:" + targetRef + ")";

    /// <summary>log: sha, unix time, author, subject. A pretty format — see above.</summary>
    public const string LogFormat = "%H%x1f%ct%x1f%an%x1f%s";

    /// <summary>
    /// Read the ref table. Local and remote refs for the same branch collapse
    /// into one line — they are one branch to the person reading, and showing
    /// "feature/x" twice is how a list of 20 branches reads as 40.
    /// </summary>
    public static IReadOnlyList<BranchLine> Refs(string output)
    {
        var byName = new Dictionary<string, BranchLine>(StringComparer.Ordinal);

        foreach (var line in Lines(output))
        {
            var parts = line.Split(Sep);
            if (parts.Length < 5) continue;

            var (name, side, remoteRef) = ShortName(parts[0]);
            if (name is null) continue;

            var counts = parts.Length > 5 ? AheadBehind(parts[5]) : null;

            var entry = new BranchLine
            {
                Name = name,
                Sha = parts[1].Trim(),
                Updated = Time(parts[2]),
                Author = parts[3].Trim(),
                Subject = parts[4].Trim(),
                Side = side,
                RemoteRef = remoteRef,
                Ahead = counts?.Ahead,
                Behind = counts?.Behind,
            };

            if (!byName.TryGetValue(name, out var seen))
            {
                byName[name] = entry;
                continue;
            }

            // Both sides exist. Keep whichever commit is newer — that is the one
            // being asked about — but remember the branch is on both.
            var newer = entry.Updated >= seen.Updated ? entry : seen;
            byName[name] = newer with
            {
                Side = seen.Side | entry.Side,
                RemoteRef = seen.RemoteRef ?? entry.RemoteRef,
                Ahead = newer.Ahead ?? seen.Ahead ?? entry.Ahead,
                Behind = newer.Behind ?? seen.Behind ?? entry.Behind,
            };
        }

        return [.. byName.Values.OrderByDescending(b => b.Updated)];
    }

    /// <summary>Branch names from `git branch --merged`, remote prefixes stripped.</summary>
    public static IReadOnlySet<string> Merged(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in Lines(output))
        {
            var raw = line.Trim().TrimStart('*', ' ');
            if (raw.Length == 0 || raw.Contains("HEAD ->", StringComparison.Ordinal)) continue;

            if (ShortName(raw).Name is { } name) names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Merge commits on a deploy branch, as "what landed, and when".
    ///
    /// Read from the first-parent history, which is that branch's own spine: a
    /// merge reachable only through some *other* branch's history landed there,
    /// not here, and listing it as a deploy to this branch would be wrong.
    /// </summary>
    public static IReadOnlyList<MergeEvent> Merges(string target, string output)
    {
        var events = new List<MergeEvent>();

        foreach (var line in Lines(output))
        {
            var parts = line.Split(Sep);
            if (parts.Length < 4) continue;

            var subject = parts[3].Trim();
            var (source, into) = Merge(subject);
            if (source is null) continue;

            // "Merge branch 'main' into feature/x" genuinely sits on main's
            // first-parent spine after feature/x is fast-forwarded back in — but
            // it records main going *out*, not something arriving. Read as a
            // landing it produces a nonsense "main → main" row, and it steals
            // the landing date from the branch that really did arrive.
            if (into is not null && !string.Equals(into, target, StringComparison.Ordinal)) continue;

            events.Add(new MergeEvent(
                Target: target,
                Source: source,
                At: Time(parts[1]),
                Author: parts[2].Trim(),
                Subject: subject,
                Sha: parts[0].Trim()));
        }

        return [.. events.OrderByDescending(e => e.At)];
    }

    /// <summary>
    /// The branch a merge commit brought in, from its subject.
    ///
    /// Covers what git, GitHub and GitLab actually write. Anything else returns
    /// null and is left out rather than guessed at — a timeline row naming the
    /// wrong branch is worse than a missing one.
    /// </summary>
    public static string? SourceOf(string subject) => Merge(subject).Source;

    /// <summary>
    /// What a merge commit brought in, and what it brought it into.
    ///
    /// The second half matters: git writes the same sentence for a merge *out*
    /// of a branch as into it, and only the "into" name distinguishes them.
    /// Anything unrecognised returns nulls and is left out rather than guessed
    /// at — a timeline row naming the wrong branch is worse than a missing one.
    /// </summary>
    public static (string? Source, string? Into) Merge(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return (null, null);

        // GitHub: Merge pull request #412 from acme/feature/x
        const string pull = "Merge pull request #";
        if (subject.StartsWith(pull, StringComparison.Ordinal))
        {
            var from = subject.IndexOf(" from ", StringComparison.Ordinal);
            if (from < 0) return (null, null);

            var slug = subject[(from + 6)..].Trim();
            var space = slug.IndexOf(' ');
            if (space > 0) slug = slug[..space];

            // owner/branch — and the branch may itself contain slashes.
            var slash = slug.IndexOf('/');
            return (slash > 0 && slash < slug.Length - 1 ? slug[(slash + 1)..] : Blank(slug), null);
        }

        // git / GitLab: Merge branch 'feature/x'
        //               Merge branch 'feature/x' into main
        //               Merge branch 'feature/x' into 'main'
        //               Merge remote-tracking branch 'origin/feature/x'
        var quote = subject.IndexOf('\'');
        if (quote < 0) return (null, null);

        var end = subject.IndexOf('\'', quote + 1);
        if (end <= quote + 1) return (null, null);

        var name = Strip(subject[(quote + 1)..end].Trim());
        var rest = subject[(end + 1)..];

        const string marker = " into ";
        var at = rest.IndexOf(marker, StringComparison.Ordinal);
        var into = at < 0 ? null : Strip(rest[(at + marker.Length)..].Trim().Trim('\''));

        return (Blank(name), Blank(into ?? string.Empty));
    }

    /// <summary>Drop a remote prefix: origin/feature/x and feature/x are one branch.</summary>
    private static string Strip(string name) =>
        name.StartsWith("origin/", StringComparison.Ordinal) ? name["origin/".Length..] : name;

    /// <summary>`%(ahead-behind:…)` output for one ref: "3 12".</summary>
    public static (int Ahead, int Behind)? AheadBehind(string? field)
    {
        var parts = (field ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;

        return int.TryParse(parts[0], out var ahead) && int.TryParse(parts[1], out var behind)
            ? (ahead, behind)
            : null;
    }

    /// <summary>
    /// Read `rev-parse --is-bare-repository --is-inside-work-tree` back.
    ///
    /// Two lines, in the order they were asked for. Both answers matter: the
    /// first says whether asking about uncommitted work is even meaningful, and
    /// either being "true" is what makes this a repository at all — a bare copy
    /// answers false to the work-tree question every time, and treating that as
    /// "not a repository" is what would break reading from a URL entirely.
    /// </summary>
    public static (bool Repository, bool Bare) Kind(string output)
    {
        var lines = Lines(output).Select(l => l.Trim()).ToList();

        var bare = lines.Count > 0 && lines[0].Equals("true", StringComparison.OrdinalIgnoreCase);
        var repo = lines.Any(l => l.Equals("true", StringComparison.OrdinalIgnoreCase));

        return (repo, bare);
    }

    /// <summary>Current branch and dirty-file count from `status --porcelain=v2 --branch`.</summary>
    public static (string? Branch, int Dirty) Status(string output)
    {
        string? branch = null;
        var dirty = 0;

        foreach (var line in Lines(output))
        {
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var name = line["# branch.head ".Length..].Trim();
                branch = name is "(detached)" ? null : name;
                continue;
            }

            if (!line.StartsWith('#')) dirty++;
        }

        return (branch, dirty);
    }

    /// <summary>Does this git support %(ahead-behind:…)? It arrived in 2.41.</summary>
    public static bool SupportsAheadBehind(string versionOutput)
    {
        // "git version 2.43.0.windows.1"
        var number = (versionOutput ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.Length > 0 && char.IsAsciiDigit(p[0]));

        if (number is null) return false;

        var bits = number.Split('.');
        if (bits.Length < 2) return false;

        return int.TryParse(bits[0], out var major) && int.TryParse(bits[1], out var minor)
               && (major > 2 || (major == 2 && minor >= 41));
    }

    /// <summary>
    /// Put the branches together with what they have reached.
    ///
    /// The merged sets are the truth — ancestry, not names. The merge events add
    /// *when*, which ancestry alone cannot say. A branch brought in by a squash
    /// has no ancestry link at all, so it reads as not merged even though its
    /// name appears in the timeline; that is a property of squash merges, not
    /// something to paper over by trusting the name — which would report a
    /// deleted-and-recreated branch as landed when it is not.
    /// </summary>
    public static IReadOnlyList<BranchLine> Standing(
        IReadOnlyList<BranchLine> branches,
        IReadOnlyDictionary<string, string> deployed,
        IReadOnlyDictionary<string, IReadOnlySet<string>?> merged,
        IReadOnlyList<MergeEvent> merges)
    {
        var result = new List<BranchLine>(branches.Count);

        foreach (var branch in branches)
        {
            var standings = new List<TargetStanding>();

            foreach (var (target, environment) in deployed)
            {
                // A deploy branch is not "merged into itself"; it is the thing.
                if (string.Equals(target, branch.Name, StringComparison.Ordinal)) continue;

                // A target with no set at all was never successfully asked
                // about — say so, rather than answering "no" on its behalf.
                var state = !merged.TryGetValue(target, out var set) || set is null
                    ? Landed.Unknown
                    : set.Contains(branch.Name) ? Landed.Yes : Landed.No;

                var landedAt = merges
                    .Where(m => m.Target == target && m.Source == branch.Name)
                    .Select(m => (DateTimeOffset?)m.At)
                    .FirstOrDefault();

                standings.Add(new TargetStanding(target, environment, state, landedAt));
            }

            result.Add(branch with
            {
                IsTarget = deployed.ContainsKey(branch.Name),
                Standings = standings,
            });
        }

        // Deploy branches first — they are what everything else is measured
        // against — then whatever changed most recently.
        return [.. result.OrderByDescending(b => b.IsTarget).ThenByDescending(b => b.Updated)];
    }

    private static IEnumerable<string> Lines(string output) =>
        (output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0);

    private static (string? Name, RefSide Side, string? RemoteRef) ShortName(string refname)
    {
        var name = refname.Trim();

        if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            return (Blank(name["refs/heads/".Length..]), RefSide.Local, null);

        if (name.StartsWith("refs/remotes/", StringComparison.Ordinal))
            return Remote(name["refs/remotes/".Length..], name);

        if (name.StartsWith("remotes/", StringComparison.Ordinal))
            return Remote(name["remotes/".Length..], "refs/" + name);

        // A bare name, as `git branch --merged` prints it.
        return name.Length == 0 || name.StartsWith("refs/", StringComparison.Ordinal)
            ? (null, RefSide.None, null)
            : (name, RefSide.Local, null);
    }

    /// <summary>
    /// Strip the remote name off "origin/feature/x".
    ///
    /// origin/HEAD is dropped: it is a symbolic pointer at the remote's default
    /// branch, not a branch of its own, and letting it through puts a phantom
    /// row called "HEAD" in the list — which then reads as a real branch nobody
    /// can find.
    /// </summary>
    private static (string? Name, RefSide Side, string? RemoteRef) Remote(string rest, string full)
    {
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return (null, RefSide.None, null);

        var name = rest[(slash + 1)..];
        return name is "HEAD"
            ? (null, RefSide.None, null)
            : (Blank(name), RefSide.Remote, full);
    }

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    private static DateTimeOffset Time(string unix) =>
        long.TryParse(unix.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
            : DateTimeOffset.MinValue;
}
