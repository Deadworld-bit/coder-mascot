namespace CoderMascot.Core;

/// <summary>One deploy branch being edited: the branch, and what reaching it means.</summary>
public sealed class DeployDraft
{
    public string Branch { get; set; } = string.Empty;

    /// <summary>"production", "staging" — what the user calls the place it lands.</summary>
    public string Environment { get; set; } = string.Empty;
}

/// <summary>
/// A watched repository part-way through being described.
///
/// The editor works on this rather than on <see cref="ProjectEntry"/> for two
/// reasons. It is ordered — the deploy list is a list, so "which branch are the
/// ahead/behind counts measured against" is the one at the top, visibly, rather
/// than a fact hidden in a dictionary's enumeration order. And it is allowed to
/// be incomplete: half a path and no branches yet is the normal state of this
/// object for as long as the window is open, which is not a state the config
/// file should ever be in.
///
/// Everything here is deliberately free of WPF, so the rules that decide what
/// can be saved are the same rules the tests check.
/// </summary>
public sealed class ProjectDraft
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public RepoHost Host { get; set; } = RepoHost.Workspace;

    /// <summary>The heading it is filed under in the rail. Blank is a group too.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>In order. The first is what counts are measured against.</summary>
    public List<DeployDraft> Deploys { get; } = [];

    /// <summary>
    /// Identity of the entry this is replacing, or null when it is a new one.
    ///
    /// Kept because the path is editable: correcting a typo in the folder of an
    /// existing project changes its identity, and matching on the *new* identity
    /// at save time would leave the old row behind and add a second one.
    /// </summary>
    public string? Replacing { get; init; }

    public bool IsNew => Replacing is null;

    public static ProjectDraft Blank(RepoHost host) => new() { Host = host };

    public static ProjectDraft From(ProjectEntry entry)
    {
        var project = entry.ToProject();

        var draft = new ProjectDraft
        {
            Replacing = project.Id,
            Name = entry.Name ?? string.Empty,
            Path = entry.Path ?? string.Empty,
            Host = project.Host,
            Group = entry.Group ?? string.Empty,
        };

        // Primary first, then the rest in the order the file had them, so the
        // list on screen reads the same way twice running.
        var deployed = project.Deployed;
        var primary = project.PrimaryTarget;

        if (primary is { Length: > 0 } && deployed.TryGetValue(primary, out var env))
            draft.Deploys.Add(new DeployDraft { Branch = primary, Environment = env });

        foreach (var (branch, environment) in deployed)
        {
            if (string.Equals(branch, primary, StringComparison.Ordinal)) continue;
            draft.Deploys.Add(new DeployDraft { Branch = branch, Environment = environment });
        }

        return draft;
    }

    public ProjectEntry ToEntry() => new ProjectEntry
    {
        Name = Name,
        Path = Path,
        Where = Host switch
        {
            RepoHost.Local => "local",
            RepoHost.Remote => "remote",
            _ => "workspace",
        },
        Deployed = Deploys
            .Where(d => !string.IsNullOrWhiteSpace(d.Branch))
            .GroupBy(d => d.Branch.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Environment?.Trim() ?? string.Empty),

        // The top of the list, named. Tidy() drops it again if that branch is
        // not among the deploy targets after its own cleaning.
        Primary = Deploys.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Branch))?.Branch.Trim(),

        // Tidy() runs Clean over it, so a heading typed with a stray tab or
        // forty words in it cannot reach the file or the rail.
        Group = Group,
    }.Tidy();

    /// <summary>Why this can't be saved yet, or null when it can.</summary>
    public string? Problem
    {
        get
        {
            var path = Path.Trim();

            // A URL is checked by the one place that decides what git may be
            // pointed at — a stricter question than "is this a folder", because
            // this string becomes an address something will connect to.
            if (Host == RepoHost.Remote) return GitUrl.Inspect(path).Problem;

            if (path.Length == 0) return "Fill in the folder this repository is in.";

            // Not cosmetic: a Windows path handed to `coder ssh` becomes a
            // Linux path that does not exist, and the error comes back as
            // "doesn't exist in the workspace" — which reads as the folder
            // being missing rather than as the wrong kind of path.
            if (Host == RepoHost.Workspace && LooksLikeWindowsPath(path))
                return "That's a Windows path. A repository in the workspace has a Linux path, "
                       + "like /home/coder/workspace/projects/my-app \u2014 or switch to \"on this PC\".";

            if (Host == RepoHost.Local && path.StartsWith('/'))
                return "That looks like a workspace path. Switch to \"in the workspace\", "
                       + "or give the folder on this PC, like C:\\src\\my-app.";

            return null;
        }
    }

    /// <summary>
    /// Something worth saying that is nonetheless not a reason to refuse a save.
    ///
    /// A repository with no deploy branches is a legitimate thing to watch — you
    /// get the branch list and the ahead/behind counts — it just cannot answer
    /// the question the window exists for, and silently not answering it is how
    /// someone concludes the feature does not work.
    /// </summary>
    public string? Advice => Deploys.Count(d => !string.IsNullOrWhiteSpace(d.Branch)) == 0
        ? "No deploy branches yet, so this repository can list its branches but can't say what has shipped. "
          + "Add the branch your CI deploys from."
        : null;

    /// <summary>Add a deploy branch. False when it is blank or already listed.</summary>
    public bool Add(string? branch, string? environment)
    {
        var name = branch?.Trim();
        if (string.IsNullOrEmpty(name)) return false;

        // Ordinal: git branch names are case-sensitive, so Main and main really
        // are two branches, and only one of them is the one being deployed.
        if (Deploys.Any(d => string.Equals(d.Branch.Trim(), name, StringComparison.Ordinal))) return false;

        var label = environment?.Trim();
        Deploys.Add(new DeployDraft
        {
            Branch = name,
            Environment = string.IsNullOrEmpty(label) ? Environments.Suggest(name) : label,
        });

        return true;
    }

    public void Remove(DeployDraft deploy) => Deploys.Remove(deploy);

    /// <summary>Move a deploy branch to the top, making it what counts run against.</summary>
    public void MakePrimary(DeployDraft deploy)
    {
        if (!Deploys.Remove(deploy)) return;
        Deploys.Insert(0, deploy);
    }

    /// <summary>The folder's own name, which is the repository's name nine times in ten.</summary>
    public string FolderName()
    {
        var path = Path.Trim();
        if (path.Length == 0) return string.Empty;

        if (Host == RepoHost.Remote) return GitUrl.Slug(path);

        var parts = path.TrimEnd('/', '\\').Split('/', '\\');
        return parts.LastOrDefault(p => p.Length > 0) ?? string.Empty;
    }

    private static bool LooksLikeWindowsPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        (path.Length > 1 && char.IsAsciiLetter(path[0]) && path[1] == ':');
}

/// <summary>
/// A first guess at what a branch means, for the environment box.
///
/// A guess, and only ever a default the user is looking straight at — the whole
/// reason deploy branches are configured by hand is that a rule like "anything
/// called release*" is right until the day a repository calls its live branch
/// Golive-AcmeSign. Anything not recognised falls through to the branch's own
/// name, which is honest: it says exactly what it knows and no more.
/// </summary>
public static class Environments
{
    /// <summary>Offered in the environment box, in the order they usually appear.</summary>
    public static readonly string[] Common =
        ["production", "staging", "uat", "qa", "test", "development", "preview"];

    public static string Suggest(string branch)
    {
        var name = branch.Trim();

        return name.ToLowerInvariant() switch
        {
            "main" or "master" or "prod" or "production" or "live" => "production",
            "staging" or "stage" => "staging",
            "develop" or "dev" or "development" => "development",
            "uat" => "uat",
            "qa" => "qa",
            "test" or "testing" => "test",
            _ => name,
        };
    }
}
