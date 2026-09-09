namespace CoderMascot.Core;

/// <summary>Runs one git command in one repository and hands back what it printed.</summary>
public delegate Task<GitOutput> GitRun(
    GitProject project, IReadOnlyList<string> args, CancellationToken ct);

/// <summary>What a git command printed, and whether it worked.</summary>
public readonly record struct GitOutput(bool Ok, string Text, string Error)
{
    public static GitOutput Good(string text) => new(true, text, string.Empty);
    public static GitOutput Bad(string error) => new(false, string.Empty, error);
}

/// <summary>
/// Reading a repository's branch and merge state.
///
/// The sequence is deliberately short — six commands for a two-target project —
/// because it may be running over `coder ssh`, where every command is a round
/// trip and a per-branch loop would take a minute. Everything that can be asked
/// once for all branches is: the ref table (with ahead/behind for every branch
/// in a single pass), one merged-set per deploy target, one merge log per
/// target.
///
/// It never fetches. A read that reaches the network needs credentials, can
/// block for a long time, and would turn opening a window into an operation
/// with side effects; the Refresh button in the UI is where a fetch belongs.
/// </summary>
public sealed class GitReader
{
    private readonly GitRun _run;

    /// <summary>Remembered per repository: whether this git can do %(ahead-behind:…).</summary>
    private readonly Dictionary<string, bool> _counts = [];

    public GitReader(GitRun run) => _run = run;

    /// <summary>How far back the merge timeline goes. A year of history is plenty.</summary>
    public int TimelineDays { get; init; } = 365;

    /// <summary>Most merge events to read per deploy branch.</summary>
    public int TimelineLimit { get; init; } = 400;

    public async Task<ProjectSnapshot> ReadAsync(GitProject project, CancellationToken ct)
    {
        var empty = new ProjectSnapshot
        {
            Name = project.Name,
            Path = project.Path,
            Host = project.Host,
            Deployed = project.Deployed,
        };

        // Cheap, and it answers "is this a repository at all" before anything
        // else is attempted — the difference between "no such path" and "git
        // isn't installed" is the whole error message.
        var inside = await _run(project, ["rev-parse", "--is-bare-repository", "--is-inside-work-tree"], ct);
        var (repository, bare) = GitParse.Kind(inside.Text);

        if (!inside.Ok || !repository) return empty with { Problem = Explain(inside, project) };

        // A bare copy has no working tree, so `status` fails outright there.
        // Skipping it is not a loss: "on main, 2 uncommitted" describes a place
        // you work, and nobody works in the copy this app fetched for itself.
        var (current, dirty) = (null as string, 0);

        if (!bare)
        {
            var status = await _run(project, ["status", "--porcelain=v2", "--branch"], ct);
            (current, dirty) = GitParse.Status(status.Text);
        }

        var branches = await ReadRefsAsync(project, ct);
        if (branches.Count == 0)
            return empty with { Problem = "No branches found in this repository." };

        // Null means "we could not find out", which is a different answer from
        // an empty set and must stay different all the way to the screen.
        var merged = new Dictionary<string, IReadOnlySet<string>?>(StringComparer.Ordinal);
        var merges = new List<MergeEvent>();
        var missed = new List<string>();

        foreach (var target in project.Deployed.Keys)
        {
            if (ct.IsCancellationRequested) break;

            // The remote ref is the one CI deploys from; a local branch may be
            // behind it, or may not exist at all on this machine. Falling back
            // to the local one keeps a repo with no remote working.
            var reference = Resolve(target, branches);

            var mergedOut = await _run(project,
                ["branch", "-a", "--merged", reference, "--format=%(refname)"], ct);

            if (mergedOut.Ok) merged[target] = GitParse.Merged(mergedOut.Text);
            else missed.Add(target);

            var logOut = await _run(project,
            [
                "log", "--first-parent", "--merges",
                $"--max-count={TimelineLimit}",
                $"--since={TimelineDays}.days",
                $"--format={GitParse.LogFormat}",
                reference,
            ], ct);

            if (logOut.Ok) merges.AddRange(GitParse.Merges(target, logOut.Text));
        }

        return empty with
        {
            CurrentBranch = current,
            DirtyFiles = dirty,
            Branches = GitParse.Standing(branches, project.Deployed, merged, merges),
            Merges = [.. merges.OrderByDescending(m => m.At)],
            Warning = missed.Count == 0
                ? null
                : $"Couldn't check what has merged into {string.Join(", ", missed)} — "
                  + "those columns say \"unknown\" rather than guessing.",
        };
    }

    /// <summary>
    /// A first look at a path somebody just typed into the Add-repository box.
    ///
    /// Deliberately not a full read: no merged sets, no timeline, three commands
    /// instead of six, because this runs while the user is still filling the
    /// form and may run again on the next keystroke-and-tab. What it must get
    /// right is the two things the form cannot know on its own — whether there
    /// is a repository at that path at all, and what its branches are called, so
    /// the deploy branches can be picked from a list instead of typed from
    /// memory into a field where a typo shows up weeks later as "not merged".
    ///
    /// It shares <see cref="Explain"/> with the real read, so a missing folder
    /// and a stopped workspace are described the same way in both places.
    /// </summary>
    public async Task<ProbeReport> ProbeAsync(GitProject project, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(project.Path))
            return ProbeReport.Failed("Fill in the folder first.");

        var inside = await _run(project, ["rev-parse", "--is-bare-repository", "--is-inside-work-tree"], ct);
        var (repository, bare) = GitParse.Kind(inside.Text);

        if (!inside.Ok || !repository) return ProbeReport.Failed(Explain(inside, project));

        var refs = await _run(project,
            ["for-each-ref", $"--format={GitParse.RefFormat}", "refs/heads", "refs/remotes"], ct);

        if (!refs.Ok) return ProbeReport.Failed(Explain(refs, project));

        // Status last, and its failure costs nothing: "on main, 2 uncommitted"
        // is decoration, and losing the whole check over it would report a
        // perfectly good repository as unusable. A bare copy is not asked at
        // all — it has no working tree to have a state.
        var (current, dirty) = (null as string, 0);

        if (!bare)
        {
            var status = await _run(project, ["status", "--porcelain=v2", "--branch"], ct);
            (current, dirty) = GitParse.Status(status.Ok ? status.Text : string.Empty);
        }

        var names = GitParse.Refs(refs.Text)
            .Select(b => b.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProbeReport(true, null, names, current, dirty);
    }

    /// <summary>
    /// The branch list, with ahead/behind counts when this git can produce them.
    ///
    /// The plain list is read first and kept as the answer. %(ahead-behind:…)
    /// counts every branch in one further command on git 2.41+, but it needs a
    /// ref to count against, and if that ref is wrong — or the field is
    /// unsupported after all — the command fails wholesale. Asking for the
    /// counts second means that failure costs the numbers, not the branch list:
    /// a repository would otherwise report "no branches" because one optional
    /// column could not be computed.
    /// </summary>
    private async Task<IReadOnlyList<BranchLine>> ReadRefsAsync(GitProject project, CancellationToken ct)
    {
        var plain = await _run(project,
            ["for-each-ref", $"--format={GitParse.RefFormat}", "refs/heads", "refs/remotes"], ct);

        if (!plain.Ok) return [];

        var branches = GitParse.Refs(plain.Text);

        if (branches.Count == 0 || project.PrimaryTarget is not { } target) return branches;
        if (ct.IsCancellationRequested || !await HasCountsAsync(project, ct)) return branches;

        var counted = await _run(project,
        [
            "for-each-ref",
            $"--format={GitParse.RefFormatWithCounts(Resolve(target, branches))}",
            "refs/heads", "refs/remotes",
        ], ct);

        if (!counted.Ok) return branches;

        var withCounts = GitParse.Refs(counted.Text);
        return withCounts.Count == branches.Count ? withCounts : branches;
    }

    private async Task<bool> HasCountsAsync(GitProject project, CancellationToken ct)
    {
        if (_counts.TryGetValue(project.Id, out var known)) return known;

        var version = await _run(project, ["--version"], ct);

        // Only a command that actually answered is worth remembering. Caching
        // the failure of a timed-out or cancelled `git --version` would leave
        // the counts blank for that repository for the rest of the session,
        // with no way back short of restarting the app.
        if (!version.Ok) return false;

        return _counts[project.Id] = GitParse.SupportsAheadBehind(version.Text);
    }

    /// <summary>
    /// The ref to measure against: the remote copy when there is one, because
    /// that is what the server deployed — a local `main` three weeks stale would
    /// otherwise report work as unmerged that has been live for a fortnight.
    /// </summary>
    private static string Resolve(string target, IReadOnlyList<BranchLine> branches)
    {
        var branch = branches.FirstOrDefault(b => b.Name == target);

        // The ref as it was actually seen, never a rebuilt "origin/…": a clone
        // whose remote is called upstream — or that has two remotes — would
        // otherwise be measured against a ref that does not exist, and every
        // command using it fails with "ambiguous argument".
        return branch?.RemoteRef ?? $"refs/heads/{target}";
    }

    private static string Explain(GitOutput output, GitProject project)
    {
        var error = output.Error.Trim();
        if (error.Length == 0) return $"{project.Path} isn't a git repository.";

        if (error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            return $"{project.Path} isn't a git repository.";

        if (error.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("cannot find the path", StringComparison.OrdinalIgnoreCase))
            return project.Host switch
            {
                RepoHost.Workspace => $"{project.Path} doesn't exist in the workspace.",
                RepoHost.Remote => "The downloaded copy of this repository is gone. "
                                   + "Press \u201cFetch from origin\u201d to fetch it again.",
                _ => $"{project.Path} doesn't exist on this machine.",
            };

        return error.Length > 200 ? error[..200] + "…" : error;
    }
}
