namespace CoderMascot.Core;

/// <summary>One branch that changed between two readings.</summary>
public sealed record BranchChange(string Name, string? Sha, string? Author, string? Subject);

/// <summary>What changed across a whole repository between two readings.</summary>
public sealed record BranchChanges
{
    public IReadOnlyList<BranchChange> New { get; init; } = [];
    public IReadOnlyList<BranchChange> Moved { get; init; } = [];
    public IReadOnlyList<BranchChange> Gone { get; init; } = [];

    public bool Any => New.Count > 0 || Moved.Count > 0 || Gone.Count > 0;
}

/// <summary>
/// What changed in a repository between two background fetches, and whether any of
/// it is worth a word.
///
/// The failure every notification has is the one to avoid here: firing on the
/// first reading after launch about a whole repository of "new" branches, or on
/// every colleague's feature branch all day. So the first reading compares against
/// nothing, a failed reading is not evidence anything moved, and only a *deploy*
/// branch moving — a merge reaching production — is news.
/// </summary>
public static class BranchDiff
{
    public static BranchChanges Between(ProjectSnapshot? previous, ProjectSnapshot next)
    {
        if (previous is null || previous.Problem is not null || next.Problem is not null)
            return new BranchChanges();

        var before = previous.Branches.ToDictionary(b => b.Name, StringComparer.Ordinal);
        var after = next.Branches.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);

        var appeared = new List<BranchChange>();
        var moved = new List<BranchChange>();
        var gone = new List<BranchChange>();

        foreach (var b in next.Branches)
        {
            if (!before.TryGetValue(b.Name, out var was))
                appeared.Add(Change(b));
            else if (!string.Equals(was.Sha, b.Sha, StringComparison.Ordinal))
                moved.Add(Change(b));
        }

        foreach (var b in previous.Branches)
            if (!after.Contains(b.Name))
                gone.Add(new BranchChange(b.Name, null, null, null));

        return new BranchChanges { New = appeared, Moved = moved, Gone = gone };
    }

    /// <summary>"1 new · 1 moved · 1 gone", or null when nothing changed.</summary>
    public static string? Summary(BranchChanges changes)
    {
        var parts = new List<string>();
        if (changes.New.Count > 0) parts.Add($"{changes.New.Count} new");
        if (changes.Moved.Count > 0) parts.Add($"{changes.Moved.Count} moved");
        if (changes.Gone.Count > 0) parts.Add($"{changes.Gone.Count} gone");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// A line about deploy branches that moved — with who and what — or null when
    /// only feature branches changed. A merge reaching production is the thing
    /// this whole watch exists to catch.
    /// </summary>
    public static string? DeployNews(string project, BranchChanges changes, IReadOnlyDictionary<string, string> deployed)
    {
        var lines = new List<string>();

        foreach (var c in changes.Moved.Concat(changes.New))
        {
            if (!deployed.TryGetValue(c.Name, out var environment)) continue;

            var what = c.Subject is { Length: > 0 } s ? $" — {s}" : "";
            var who = c.Author is { Length: > 0 } a ? $" by {a}" : "";
            lines.Add($"{environment} ({c.Name}){what}{who}");
        }

        return lines.Count == 0 ? null : $"{project}: {string.Join("; ", lines)}";
    }

    private static BranchChange Change(BranchLine b) => new(b.Name, b.Sha, b.Author, b.Subject);
}
