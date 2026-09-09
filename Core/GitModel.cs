namespace CoderMascot.Core;

/// <summary>Where a repository lives, and therefore how git is run against it.</summary>
public enum RepoHost
{
    /// <summary>A clone on this Windows machine.</summary>
    Local,

    /// <summary>A clone inside the Coder workspace, reached over `coder ssh`.</summary>
    Workspace,

    /// <summary>
    /// Nothing but a URL: the app keeps its own bare copy and reads that.
    ///
    /// The fast one, and the reason it exists. A workspace repository costs an
    /// ssh round trip per command and a local one has to be cloned with a full
    /// working tree; this fetches commits once and every read afterwards is a
    /// local process against a few megabytes. See <see cref="MirrorStore"/>.
    /// </summary>
    Remote,
}

/// <summary>One repository being watched, and what counts as deployed in it.</summary>
public sealed class GitProject
{
    public required string Name { get; init; }
    public required string Path { get; init; }

    /// <summary>
    /// What identifies this project everywhere — the cache, the selection, the
    /// "is this still the project I asked about" guard.
    ///
    /// Not the name: two checkouts of the same repository in different folders
    /// both take the folder name, and keying on that shows one repository's
    /// branches under the other's heading.
    /// </summary>
    public string Id => $"{Host}:{Path}";
    public RepoHost Host { get; init; } = RepoHost.Workspace;

    /// <summary>
    /// Branch name → what it means when something reaches it, e.g.
    /// main → "production", Golive-Redesign → "staging".
    ///
    /// Named rather than guessed. A rule like "anything called release*" is
    /// right until the day a repo calls its live branch Golive-AcmeSign, and
    /// then it is silently wrong about the only question this feature exists to
    /// answer.
    /// </summary>
    public IReadOnlyDictionary<string, string> Deployed { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Which deploy branch the ahead/behind counts are measured against, named.
    ///
    /// Named rather than "whichever comes first", because first-ness would live
    /// in a Dictionary, whose enumeration order .NET explicitly declines to
    /// promise. When it is absent we fall back to that order anyway — that is
    /// what every config written before this field relied on, and what it did.
    /// </summary>
    public string? Primary { get; init; }

    /// <summary>The heading this repository is filed under, or null.</summary>
    public string? Group { get; init; }

    /// <summary>The deploy branch ahead/behind counts are measured against.</summary>
    public string? PrimaryTarget =>
        Primary is { Length: > 0 } named && Deployed.ContainsKey(named)
            ? named
            : Deployed.Keys.FirstOrDefault();
}

/// <summary>Whether a ref exists here, there, or both.</summary>
[Flags]
public enum RefSide
{
    None = 0,
    Local = 1,
    Remote = 2,
    Both = Local | Remote,
}

/// <summary>
/// Whether a branch has reached a deploy target.
///
/// Three states, not two. A `git branch --merged` that timed out or was
/// cancelled tells us nothing, and rendering that as "not merged" is the one
/// failure this whole feature must not have: it reads as an answer, it is
/// indistinguishable from a real one, and it is wrong.
/// </summary>
public enum Landed
{
    Unknown,
    Yes,
    No,
}

/// <summary>A branch as it stands against one deploy target.</summary>
public sealed record TargetStanding(string Target, string Environment, Landed State, DateTimeOffset? LandedAt)
{
    public bool Merged => State == Landed.Yes;

    public string Label => State switch
    {
        Landed.Yes => LandedAt is { } at ? $"{Environment} · {Friendly.Since(at)}" : $"{Environment} · merged",
        Landed.No => $"{Environment} · not yet",
        _ => $"{Environment} · unknown",
    };
}

/// <summary>
/// Which of the six answers a branch is.
///
/// Six, and they must partition the list exactly: a branch that falls into two
/// groups appears twice in the window, and one that falls into none disappears
/// from it — silently, which is the worse of the two. That is the property
/// worth a test, more than any individual rule here.
/// </summary>
public enum BranchGroup
{
    /// <summary>A deploy branch. The reference, not a thing being measured.</summary>
    Deploy,

    /// <summary>Nothing has reached any deploy branch.</summary>
    Open,

    /// <summary>In some environments, not all.</summary>
    Partly,

    /// <summary>Somewhere it could not be established. Not the same as no.</summary>
    Unknown,

    /// <summary>All the way in, everywhere.</summary>
    Shipped,

    /// <summary>Shipped everywhere, local only, remote gone: the tidy-up.</summary>
    Tidy,
}

/// <summary>One branch, and everything worth knowing about it at a glance.</summary>
public sealed record BranchLine
{
    public required string Name { get; init; }
    public required string Sha { get; init; }
    public required DateTimeOffset Updated { get; init; }
    public required string Author { get; init; }
    public required string Subject { get; init; }
    public RefSide Side { get; init; } = RefSide.None;

    /// <summary>
    /// The remote-tracking ref this branch was seen at, verbatim.
    ///
    /// Carried rather than rebuilt as "refs/remotes/origin/&lt;name&gt;": a clone whose
    /// remote is called anything else — upstream, gitlab, a second remote — would
    /// otherwise be measured against a ref that does not exist, and every git
    /// command using it fails.
    /// </summary>
    public string? RemoteRef { get; init; }

    /// <summary>Commits this branch has that the primary target doesn't, and vice versa.</summary>
    public int? Ahead { get; init; }
    public int? Behind { get; init; }

    /// <summary>This branch is itself one of the deploy targets.</summary>
    public bool IsTarget { get; init; }

    public IReadOnlyList<TargetStanding> Standings { get; init; } = [];

    /// <summary>In every deploy branch. An unknown is not a yes.</summary>
    public bool MergedEverywhere => Standings.Count > 0 && Standings.All(s => s.State == Landed.Yes);

    public bool MergedSomewhere => Standings.Any(s => s.State == Landed.Yes);

    /// <summary>Something could not be determined, so the row is not the whole story.</summary>
    public bool HasUnknowns => Standings.Any(s => s.State == Landed.Unknown);

    /// <summary>
    /// A local branch whose remote is gone, already merged: the safe-to-delete
    /// case, and the one everybody forgets to do.
    /// </summary>
    public bool SafeToDelete => Side == RefSide.Local && MergedEverywhere && !IsTarget;

    /// <summary>
    /// Which heading this branch belongs under.
    ///
    /// Ordered so the tests are exclusive without having to say so: a deploy
    /// branch is never anything else, a tidy-up is by definition shipped
    /// everywhere, and nothing with an unknown in it can be shipped everywhere.
    ///
    /// A repository with no deploy branches configured has no standings at all,
    /// so every branch here reads as Open — true only in the sense that the
    /// question was never asked, which is why the window doesn't group at all
    /// when there is nothing to measure against.
    /// </summary>
    public BranchGroup Group =>
        IsTarget ? BranchGroup.Deploy
        : SafeToDelete ? BranchGroup.Tidy
        : HasUnknowns ? BranchGroup.Unknown
        : MergedEverywhere ? BranchGroup.Shipped
        : MergedSomewhere ? BranchGroup.Partly
        : BranchGroup.Open;
}

/// <summary>Something landing on a deploy branch, and when.</summary>
public sealed record MergeEvent(
    string Target, string Source, DateTimeOffset At, string Author, string Subject, string Sha);

/// <summary>Everything read from one repository in one pass.</summary>
public sealed record ProjectSnapshot
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public RepoHost Host { get; init; }

    /// <summary>Null while it has never been read; a sentence when reading failed.</summary>
    public string? Problem { get; init; }

    /// <summary>
    /// Something went wrong that did not invalidate the reading — a fetch that
    /// couldn't reach the server, one merged-set that failed. Kept apart from
    /// Problem because Problem blanks the window, and throwing away a perfectly
    /// good branch list because the network was down is not a trade worth making.
    /// </summary>
    public string? Warning { get; init; }

    public string? CurrentBranch { get; init; }
    public int DirtyFiles { get; init; }
    public DateTimeOffset ReadAt { get; init; } = DateTimeOffset.Now;

    public IReadOnlyList<BranchLine> Branches { get; init; } = [];
    public IReadOnlyList<MergeEvent> Merges { get; init; } = [];
    public IReadOnlyDictionary<string, string> Deployed { get; init; } = new Dictionary<string, string>();

    public bool Ok => Problem is null;

    /// <summary>
    /// Branches with work that has not reached every deploy target.
    ///
    /// Zero when the project names no deploy branches: a repository that was
    /// never asked the question has not failed it, and a badge saying "27" for
    /// one is noise that trains you to ignore the badge.
    /// </summary>
    public int Outstanding =>
        Deployed.Count == 0 ? 0 : Branches.Count(b => !b.IsTarget && !b.MergedEverywhere);
}

/// <summary>
/// Whether the server can be reached and signed in to, said in one sentence.
///
/// Its own question, deliberately separate from reading a repository, because
/// when a download fails there are four different things it could mean — the
/// URL, the network, the sign-in, or the account's access — and a window that
/// cannot tell them apart leaves the user changing things at random. This asks
/// the server directly, downloads nothing, and needs no local copy to exist.
/// </summary>
public sealed record RemoteTest(bool Reachable, int Branches, string Summary, string? Detail)
{
    public static RemoteTest Failed(string summary, string? detail) => new(false, 0, summary, detail);

    public static RemoteTest From(bool ok, string output, string error, string url)
    {
        if (ok)
        {
            var heads = (output ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.Contains("refs/heads/", StringComparison.Ordinal));

            return new(true, heads,
                heads == 0
                    ? "Reached the server and signed in \u2014 but the repository has no branches."
                    : $"Reached the server and signed in \u2014 {heads} branch{(heads == 1 ? "" : "es")} there. "
                      + "Press Download to fetch it.",
                null);
        }

        var host = HostOf(url);

        var summary =
            GitSignIn.Unreachable(error)
                ? $"Couldn't reach {host} at all. Check the address, and whether this PC needs a VPN for it."
            : GitSignIn.Untrusted(error)
                ? $"Reached {host}, but git doesn't trust its certificate on this PC."
            : GitSignIn.Forbidden(error)
                ? $"Signed in to {host}, but this account can't see that repository. "
                  + "Check the path is right, and that the token has read access to it."
            : GitSignIn.Needed(error)
                ? $"Reached {host}, but the sign-in was refused. "
                  + "Fill in the username and token below and test again."
            : $"git couldn't read that repository. The details are below.";

        return new(false, 0, summary, error);
    }

    private static string HostOf(string url)
    {
        var text = url ?? string.Empty;

        var start = text.IndexOf("://", StringComparison.Ordinal);
        start = start < 0 ? 0 : start + 3;

        var rest = text[start..];
        var end = rest.IndexOfAny(['/', ':']);
        var host = end < 0 ? rest : rest[..end];

        var at = host.LastIndexOf('@');
        if (at >= 0) host = host[(at + 1)..];

        return host.Length == 0 ? "the server" : host;
    }
}

/// <summary>
/// What a fetch did — which is three answers, not two.
///
/// A fetch can come back non-zero having done most of its job: some branch names
/// cannot be written to a Windows filesystem, and git reports that as a failure
/// of the whole command. Collapsing that into "it failed" throws away every
/// branch that did arrive; collapsing it into "it worked" hides a branch that
/// will never appear and never be explained. So there is a third state, and it
/// carries a sentence.
/// </summary>
public sealed record FetchOutcome(string? Problem, string? Warning)
{
    public static readonly FetchOutcome Done = new(null, null);

    public static FetchOutcome Failed(string problem) => new(problem, null);

    public static FetchOutcome Partial(string warning) => new(null, warning);

    public bool Ok => Problem is null;
}

/// <summary>
/// A quick look at a path before it is committed to the config: is there a
/// repository there, and what is in it?
///
/// Separate from <see cref="ProjectSnapshot"/> because it answers a different
/// question. A snapshot is a reading of a project you already have; this is the
/// check that decides whether you have one at all, and it has to survive being
/// pointed at a typo, a stopped workspace, and a folder that is not a checkout.
/// </summary>
public sealed record ProbeReport(
    bool Ok, string? Problem, IReadOnlyList<string> Branches, string? CurrentBranch, int DirtyFiles,
    string? Warning = null)
{
    public static ProbeReport Failed(string problem) => new(false, problem, [], null, 0);

    /// <summary>What the check found, in one line, for the panel under the box.</summary>
    public string Summary
    {
        get
        {
            if (!Ok) return Problem ?? "Couldn't read that path.";

            // Worth its own sentence rather than "0 branches", which reads as a
            // measurement of an empty repository when it is far more often the
            // shape of something having gone wrong upstream of the count.
            var line = "Found a repository, but it has no branches in it.";

            if (Branches.Count > 0)
            {
                var facts = new List<string> { Branches.Count == 1 ? "1 branch" : $"{Branches.Count} branches" };
                if (CurrentBranch is { Length: > 0 } on) facts.Add($"on {on}");
                if (DirtyFiles > 0) facts.Add($"{DirtyFiles} uncommitted");

                line = "Found a repository \u2014 " + string.Join(" \u00b7 ", facts) + ".";
            }

            // A download that mostly worked says so here rather than nowhere: the
            // count above is right, and is not the whole story.
            return Warning is { Length: > 0 } note ? line + " " + note : line;
        }
    }
}

/// <summary>
/// A watched repository as it appears in config.json.
///
/// Separate from <see cref="GitProject"/> so the file format is a plain,
/// forgiving shape — string paths, a string for where it lives, a plain
/// name → environment map — while the rest of the code works with something
/// already validated.
/// </summary>
public sealed class ProjectEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>
    /// "workspace" (over coder ssh), "local" (a clone on this PC), or "remote"
    /// (a URL, read from a copy this app fetches for itself).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("where")]
    public string? Where { get; set; }

    /// <summary>Branch name → environment, e.g. {"main": "production"}.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("deployed")]
    public Dictionary<string, string>? Deployed { get; set; }

    /// <summary>Which of those branches the ahead/behind counts run against.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("primary")]
    public string? Primary { get; set; }

    /// <summary>
    /// The heading this repository is filed under in the rail, or blank.
    ///
    /// A plain string rather than an id into a list of groups defined elsewhere:
    /// see <see cref="ProjectGroups"/> for why that is the whole design and not
    /// a shortcut.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("group")]
    public string? Group { get; set; }

    /// <summary>Fill in what can be inferred, drop what can't be used.</summary>
    public ProjectEntry Tidy()
    {
        Path = Path?.Trim();

        // Exactly one of three spellings from here on. ToProject() treats
        // anything unrecognised as a workspace repo, so a null and a "workspace"
        // describe the same project — and any list deduplicated on the raw
        // string would keep both, as two rows sharing one identity.
        Where = (Where ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "local" => "local",
            "remote" or "url" or "git" => "remote",
            _ => "workspace",
        };

        // A URL is stored the way it will be used: without the password somebody
        // pasted along with it. config.json is plain settings, and this app does
        // not have a place to keep a secret — Git's own credential manager does.
        if (Where == "remote" && Path is { Length: > 0 } && GitUrl.Clean(Path) is { } clean)
            Path = clean;

        // The folder name is the repository's name nine times out of ten, and
        // making someone write it twice is how a config file grows a typo.
        if (string.IsNullOrWhiteSpace(Name) && Path is { Length: > 0 })
        {
            Name = Where == "remote"
                ? GitUrl.Slug(Path)
                : Path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault();
        }

        if (string.IsNullOrWhiteSpace(Name)) Name = "repository";

        Deployed = Deployed?
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .ToDictionary(kv => kv.Key.Trim(),
                          kv => string.IsNullOrWhiteSpace(kv.Value) ? kv.Key.Trim() : kv.Value.Trim());

        // A primary naming a branch that is no longer a deploy target is worse
        // than none: it would silently measure nothing while looking configured.
        Primary = Primary?.Trim();
        if (Primary is { Length: > 0 } && Deployed?.ContainsKey(Primary) != true) Primary = null;

        Group = ProjectGroups.Clean(Group);

        return this;
    }

    public GitProject ToProject() => new()
    {
        Name = Name ?? "repository",
        Path = Path ?? string.Empty,
        Host = (Where ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "local" => RepoHost.Local,
            "remote" or "url" or "git" => RepoHost.Remote,
            _ => RepoHost.Workspace,
        },
        Deployed = Deployed ?? new Dictionary<string, string>(),
        Primary = Primary,
        Group = Group,
    };
}
