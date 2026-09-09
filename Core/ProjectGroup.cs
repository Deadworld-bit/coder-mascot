namespace CoderMascot.Core;

/// <summary>A heading in the project rail, and the projects filed under it.</summary>
/// <param name="Named">
/// False for the leftovers band. The window draws no heading at all when the
/// only band is that one, so a person who never groups anything sees exactly
/// the list they saw before groups existed.
/// </param>
public sealed record ProjectBand<T>(string Title, string Key, bool Named, IReadOnlyList<T> Items);

/// <summary>
/// Filing watched repositories under headings.
///
/// A group is a plain string on the project, not an entry in a list of group
/// objects somewhere else. That choice is the whole design: there is no such
/// thing here as a group that exists with nothing in it, or a project pointing
/// at a group that has been deleted, so none of the states that need repairing
/// can be reached. Renaming a group is retyping the word; removing one is
/// clearing it off the last project that used it.
///
/// The cost is that groups cannot be reordered by hand — they appear in the
/// order their first project does — and that is a fair trade for a settings
/// file somebody is expected to be able to edit in Notepad.
/// </summary>
public static class ProjectGroups
{
    /// <summary>The key of the band for projects with no group.</summary>
    public const string Loose = "";

    /// <summary>What that band is called once there is anything to contrast it with.</summary>
    public const string LooseTitle = "Not in a group";

    /// <summary>
    /// Long enough for "Mắt Bão Cloud", short enough that the rail stays a rail.
    /// </summary>
    public const int MaxLength = 32;

    /// <summary>The name as it will be stored and shown, or null for no group.</summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // A group name is written to a settings file and drawn into a heading.
        // A stray newline or tab in it would break both, so whitespace of every
        // kind collapses to one plain space and control characters are dropped.
        var text = new System.Text.StringBuilder(raw.Length);
        var space = false;

        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c)) { space = text.Length > 0; continue; }
            if (char.IsControl(c)) continue;

            if (space) { text.Append(' '); space = false; }
            text.Append(c);
        }

        var name = text.ToString();
        if (name.Length == 0) return null;

        return name.Length <= MaxLength ? name : name[..MaxLength].TrimEnd();
    }

    /// <summary>
    /// What decides two projects are in the same group.
    ///
    /// Case-insensitive, so typing "backend" once and "Backend" the next time
    /// files both under one heading rather than growing a second group that
    /// looks identical in the rail and behaves as though it isn't.
    /// </summary>
    public static string Key(string? raw) => Clean(raw)?.ToLowerInvariant() ?? Loose;

    public static bool Same(string? a, string? b) => Key(a) == Key(b);

    /// <summary>
    /// Every group in use, spelled the way it was first spelled, in the order
    /// their projects appear. What the group box offers, so joining an existing
    /// group is a pick rather than a retype that might not match.
    /// </summary>
    public static IReadOnlyList<string> Names(IEnumerable<string?>? raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();

        foreach (var candidate in raw ?? [])
        {
            if (Clean(candidate) is not { } name) continue;
            if (seen.Add(Key(name))) names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// One spelling per group, for a whole list at once.
    ///
    /// Applied when the list is saved, so the file itself ends up consistent and
    /// the rail is not the only place the merge appears to have happened.
    /// </summary>
    public static string? Canonical(string? raw, IEnumerable<string?>? amongst)
    {
        if (Clean(raw) is not { } name) return null;

        var key = Key(name);
        foreach (var other in Names(amongst))
            if (Key(other) == key) return other;

        return name;
    }

    /// <summary>
    /// The folded-shut headings that still exist, and nothing else.
    ///
    /// A group renamed or emptied leaves its key behind, and a key nobody can
    /// see is a heading that folds itself shut the day somebody reuses the name.
    /// </summary>
    public static string[] Live(IEnumerable<string?>? collapsed, IEnumerable<string?>? groups)
    {
        var known = new HashSet<string>(
            (groups ?? []).Select(Key).Where(k => k != Loose), StringComparer.Ordinal);

        return [.. (collapsed ?? [])
            .Select(Key)
            .Where(known.Contains)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The list, split into bands.
    ///
    /// Named groups first in the order their first project appears, leftovers
    /// last. Every item comes out exactly once: a project in two bands is a
    /// project shown twice, and one in no band vanishes from the rail without
    /// saying so, which is the worse of the two and the reason this is a
    /// function with a test rather than a loop in the window.
    /// </summary>
    public static IReadOnlyList<ProjectBand<T>> Arrange<T>(
        IEnumerable<T>? items, Func<T, string?> groupOf)
    {
        var order = new List<string>();
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var filed = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        var loose = new List<T>();

        foreach (var item in items ?? [])
        {
            if (Clean(groupOf(item)) is not { } name) { loose.Add(item); continue; }

            var key = Key(name);
            if (!filed.TryGetValue(key, out var band))
            {
                filed[key] = band = [];
                titles[key] = name;
                order.Add(key);
            }

            band.Add(item);
        }

        var bands = new List<ProjectBand<T>>(order.Count + 1);
        foreach (var key in order)
            bands.Add(new ProjectBand<T>(titles[key], key, true, filed[key]));

        // Only when there is a heading to be outside of. On a list where nothing
        // is grouped this is the single band, unnamed, and the rail draws it as
        // the plain list it has always been.
        if (loose.Count > 0)
            bands.Add(new ProjectBand<T>(bands.Count == 0 ? string.Empty : LooseTitle, Loose, false, loose));

        return bands;
    }
}
