namespace CoderMascot.Core;

/// <summary>One line of the Diagrams menu: a diagram to open, or a heading with lines under it.</summary>
public sealed record DiagramMenuItem(string Header, string? Target, IReadOnlyList<DiagramMenuItem> Items)
{
    public static DiagramMenuItem Open(string header, string target) => new(header, target, []);

    public static DiagramMenuItem Folder(string header, IReadOnlyList<DiagramMenuItem> items) => new(header, null, items);
}

/// <summary>
/// Every linked diagram, laid out the way the Branches rail lays out the
/// repositories: under their group headings, in the same order.
///
/// A level is skipped wherever it would hold only one thing. A submenu of one
/// is a click that chooses nothing, and the menu hangs off a character that
/// may be walking away from the pointer.
/// </summary>
public static class DiagramMenu
{
    public static IReadOnlyList<DiagramMenuItem> Build(IEnumerable<GitProject>? projects)
    {
        var bands = ProjectGroups.Arrange((projects ?? []).Where(p => Openable(p).Any()), p => p.Group);

        if (bands.Count == 1) return [.. bands[0].Items.Select(Project)];

        return [.. bands.Select(band => DiagramMenuItem.Folder(band.Title, [.. band.Items.Select(Project)]))];
    }

    private static DiagramMenuItem Project(GitProject project)
    {
        var links = Openable(project).ToList();

        return links.Count == 1
            ? DiagramMenuItem.Open($"{project.Name} · {links[0].Label}", links[0].Target)
            : DiagramMenuItem.Folder(project.Name, [.. links.Select(l => DiagramMenuItem.Open(l.Label, l.Target))]);
    }

    private static IEnumerable<(string Label, string Target)> Openable(GitProject project) =>
        project.Diagrams
            .Where(d => d.Target is not null && DiagramLink.Inspect(d.Target).Place is not null)
            .Select(d => (d.Label, d.Target!));
}
