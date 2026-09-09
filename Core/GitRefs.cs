namespace CoderMascot.Core;

/// <summary>
/// A fetch that could not write down some of the branches it downloaded.
///
/// Git keeps one file per branch, so a branch name is also a path — and Windows
/// will not accept a path segment containing <c>" * : &lt; &gt; ? |</c>. A branch
/// called <c>Dashboard-"Lượt-ký"---Tổng-quan-/-Nhân-viên</c> therefore cannot be
/// stored on a Windows machine at all, by this app or by `git clone`.
///
/// It is a partial failure and has to be reported as one. Git exits non-zero, so
/// the naive reading is "the download failed" — but the other branches did land,
/// and blanking a repository's whole branch list because one branch has a name
/// somebody pasted out of a ticket title is the same mistake as blanking it
/// because the network blinked.
/// </summary>
public static class GitRefs
{
    /// <summary>
    /// Lock failures whose cause is the name itself, and nothing else.
    ///
    /// Deliberately short. "Permission denied" and "File exists" arrive through
    /// the identical `cannot lock ref` sentence and mean something the user has
    /// to be told about — a read-only folder, a stale lock from a git that was
    /// killed — so downgrading those to a footnote would hide a real fault
    /// behind a note about branch names.
    /// </summary>
    private static readonly string[] NameFaults =
    [
        "unable to create directory for",
        "invalid argument",
        "file name too long",
        "filename too long",
    ];

    /// <summary>Git's own summary line, which adds nothing we don't already know.</summary>
    private const string Summary = "some local refs could not be updated";

    /// <summary>The refs this machine's filesystem refused to store, in git's spelling.</summary>
    public static IReadOnlyList<string> Unstorable(string? stderr)
    {
        var found = new List<string>();

        foreach (var line in Lines(stderr))
        {
            var (name, reason) = Split(line);
            if (name is null) continue;

            if (NameFaults.Any(f => reason.Contains(f, StringComparison.OrdinalIgnoreCase)))
                found.Add(name);
        }

        return found;
    }

    /// <summary>
    /// True when every complaint git made was one of those, so the branches that
    /// did land are current and worth keeping.
    /// </summary>
    public static bool OnlyUnstorable(string? stderr)
    {
        var any = false;

        foreach (var line in Lines(stderr))
        {
            if (!line.StartsWith("error:", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)) continue;

            if (line.Contains(Summary, StringComparison.OrdinalIgnoreCase)) continue;

            var (name, reason) = Split(line);
            if (name is not null && NameFaults.Any(f => reason.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                any = true;
                continue;
            }

            // Something else went wrong as well. Whatever it is, it is not a
            // footnote, and the branches on disk may not be current because of it.
            return false;
        }

        return any;
    }

    /// <summary>What to put in front of the user, or null when nothing is wrong.</summary>
    public static string? Explain(IReadOnlyList<string>? refs)
    {
        if (refs is null || refs.Count == 0) return null;

        var names = refs.Select(Short).ToList();

        var subject = names.Count == 1
            ? $"the branch {names[0]}"
            : $"{names.Count} branches, including {names[0]}";

        return $"Windows can't store {subject} — git keeps one file per branch, and a Windows "
             + "folder name can't contain the characters \" * : < > ? | in it. It was skipped and "
             + "everything else downloaded. Renaming that branch on the server is the fix, and it "
             + "fixes `git clone` on Windows for everyone else too.";
    }

    /// <summary>
    /// The fetch that steps around those refs instead of failing on them, or
    /// null when they aren't the shape this can work with.
    ///
    /// Git takes negative refspecs, so the branch whose name this filesystem
    /// won't accept can simply be left out — and then the fetch succeeds, the
    /// other branches land, and the only thing missing is the one that could
    /// never have been written anyway. Better than being honest about a total
    /// failure, which was the most this could do before.
    ///
    /// Needs git 2.29 or newer for the <c>^</c> refspec. On anything older the
    /// retry comes back non-zero and the caller reports the original failure,
    /// which is what it would have done regardless.
    /// </summary>
    public static string[]? SkipArgs(IReadOnlyList<string>? refs)
    {
        if (refs is null || refs.Count == 0) return null;

        string? remote = null;
        var sources = new List<string>();

        foreach (var name in refs)
        {
            // refs/remotes/<remote>/<branch>, where <branch> may itself have
            // slashes in it — so the remote is the one segment after the prefix.
            const string prefix = "refs/remotes/";
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;

            var rest = name[prefix.Length..];
            var cut = rest.IndexOf('/');
            if (cut <= 0 || cut == rest.Length - 1) return null;

            var here = rest[..cut];
            if (remote is null) remote = here;
            else if (!string.Equals(remote, here, StringComparison.Ordinal)) return null;

            sources.Add("^refs/heads/" + rest[(cut + 1)..]);
        }

        if (remote is null || sources.Count == 0) return null;

        // Written out in full rather than relying on the remote's configured
        // refspec: a negative refspec on the command line replaces it, so the
        // positive one has to be there too or nothing is fetched at all.
        return
        [
            "fetch", "--prune", remote,
            $"+refs/heads/*:refs/remotes/{remote}/*",
            .. sources,
        ];
    }

    /// <summary>refs/remotes/origin/x → x, shortened enough to sit in a sentence.</summary>
    public static string Short(string refName)
    {
        var name = refName;

        foreach (var prefix in (string[])["refs/remotes/origin/", "refs/remotes/", "refs/heads/"])
            if (name.StartsWith(prefix, StringComparison.Ordinal)) { name = name[prefix.Length..]; break; }

        return name.Length <= 48 ? $"“{name}”" : $"“{name[..48]}…”";
    }

    /// <summary>The ref and the reason out of one `cannot lock ref` line.</summary>
    private static (string? Name, string Reason) Split(string line)
    {
        const string marker = "cannot lock ref '";

        var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return (null, string.Empty);

        var start = at + marker.Length;

        // The ref name can itself contain a quote, so the terminator is the
        // whole "': " sequence rather than the next apostrophe.
        var end = line.IndexOf("': ", start, StringComparison.Ordinal);
        if (end < 0) return (null, string.Empty);

        return (line[start..end], line[(end + 3)..]);
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim('\r', ' '));
}
