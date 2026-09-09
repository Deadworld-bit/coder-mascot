namespace CoderMascot.Core;

/// <summary>A rectangle in plain doubles, so the placement rules stay testable.</summary>
public readonly record struct Area(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    public double[] ToArray() => [Left, Top, Width, Height];

    public static Area? FromArray(double[]? v) =>
        v is { Length: 4 } && v.All(double.IsFinite) ? new Area(v[0], v[1], v[2], v[3]) : null;
}

/// <summary>
/// The paper a note is written on.
///
/// Colour is the one thing a sticky note has that a list row doesn't, and it is
/// what makes a wall of them readable at a glance — so it is a real value with
/// real rules, not a UI detail. Stored as a name rather than a hex string: the
/// palette can be re-tuned without rewriting everyone's notes, and an unknown
/// name falls back instead of painting a note with garbage.
/// </summary>
public sealed record NotePaper(string Id, string Paper, string Header, string Ink, string Faint);

public static class NoteColour
{
    public const string Default = "yellow";

    public static readonly IReadOnlyList<NotePaper> All =
    [
        new("yellow", "#FDE68A", "#FBBF24", "#1F2937", "#6B5B2A"),
        new("pink",   "#FBCFE8", "#F472B6", "#1F2937", "#6B2F4E"),
        new("blue",   "#BFDBFE", "#60A5FA", "#1F2937", "#27456B"),
        new("green",  "#BBF7D0", "#4ADE80", "#1F2937", "#245B3A"),
        new("purple", "#DDD6FE", "#A78BFA", "#1F2937", "#42356B"),
        new("grey",   "#E5E7EB", "#9CA3AF", "#1F2937", "#4B5563"),
    ];

    public static NotePaper Of(string? id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    /// <summary>The stored form of a colour name — never an unknown one.</summary>
    public static string Normalise(string? id) => Of(id).Id;

    /// <summary>The next colour round, for a one-click cycle on the note itself.</summary>
    public static string Next(string? id)
    {
        var at = All.ToList().FindIndex(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        return All[(at < 0 ? 0 : at + 1) % All.Count].Id;
    }
}

/// <summary>
/// Where a sticky note goes.
///
/// Two rules, both about a note you cannot get to. Windows remembers where you
/// dropped a note, and monitors get unplugged — a note restored at its saved
/// spot on a screen that no longer exists is gone for good, and it is holding
/// something you wrote down because you did not want to lose it. And a new note
/// dropped exactly under the mouse would open with the pointer already inside
/// it, so a second one lands beside the first rather than on top of it.
/// </summary>
public static class StickyPlacement
{
    public const double DefaultWidth = 240;
    public const double DefaultHeight = 200;

    /// <summary>Smallest note that still has a header and a line of text.</summary>
    public const double MinWidth = 160;
    public const double MinHeight = 120;

    /// <summary>How much of a note must stay on a screen to count as reachable.</summary>
    private const double MustShow = 60;

    public static Area Clamp(Area note, Area screen)
    {
        var width = Math.Clamp(Round(note.Width, DefaultWidth), MinWidth, Math.Max(MinWidth, screen.Width));
        var height = Math.Clamp(Round(note.Height, DefaultHeight), MinHeight, Math.Max(MinHeight, screen.Height));

        // Leave the note wherever it is as long as a grabbable piece of it is on
        // screen; only haul it back when it is genuinely unreachable.
        var left = Math.Clamp(Round(note.Left, screen.Left), screen.Left - width + MustShow, screen.Right - MustShow);

        // The top is stricter than the sides: a note dragged above the top edge
        // has its header off screen, and the header is the only way to drag it
        // back down.
        var top = Math.Clamp(Round(note.Top, screen.Top), screen.Top, screen.Bottom - MustShow);

        return new Area(left, top, width, height);
    }

    /// <summary>A new note near a point — offset so the pointer isn't inside it.</summary>
    public static Area Near(double x, double y, Area screen, int alreadyOpen = 0)
    {
        var step = 24 * (alreadyOpen % 6);
        return Clamp(new Area(x + 16 + step, y + 16 + step, DefaultWidth, DefaultHeight), screen);
    }

    private static double Round(double v, double fallback) => double.IsFinite(v) ? v : fallback;
}
