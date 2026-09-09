namespace CoderMascot.Core;

/// <summary>
/// How a character gets around the screen edges.
///
/// This is not decoration — it decides which animations exist. A walker has a
/// run and a climb and can hang off the ceiling; a flier has none of those and
/// instead has an ascend, a descend and a hover. Asking a flier for "climb"
/// gets you a missing animation and a silent fallback into the wrong pose.
/// </summary>
public enum MovementStyle
{
    /// <summary>Runs along the floor, climbs the walls, hangs from the ceiling.</summary>
    Walks,

    /// <summary>Flies the same perimeter: hovering along, ascending and descending the walls.</summary>
    Flies,
}

/// <summary>One playable character: where its frames live and how it moves.</summary>
public sealed record Character(string Id, string Display, MovementStyle Style)
{
    /// <summary>Everything the app knows how to be.</summary>
    public static readonly IReadOnlyList<Character> All =
    [
        new("pikachu", "Pikachu", MovementStyle.Walks),
        new("charizard", "Charizard", MovementStyle.Flies),
        new("mew", "Mew", MovementStyle.Flies),
    ];

    public static Character? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Frames per second per animation. A glide is slower than a dash.</summary>
    public int Fps(string animation) => animation switch
    {
        "run" => 14,
        "fly" => 12,
        "ascend" or "descend" => 12,
        "climb" => 10,
        "hang" => 10,
        "hover" => 8,
        _ => 8,          // idle, climbidle, flyidle
    };
}
