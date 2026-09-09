using CoderMascot.Core;

namespace CoderMascot.UI;

/// <summary>
/// Every colour the windows use, in one place, with a reason for each.
///
/// It exists because the alternative had grown to fifty-two hexes in two
/// different families of dark blue, and to three greens and five ambers that
/// each meant "good" and "careful" in a different place. That is what makes an
/// interface look unfinished even when the layout is right: not one bad colour,
/// but a set of colours that were clearly never chosen together.
///
/// Two rules hold it together. Every surface is the same hue, one step apart, so
/// depth reads as depth rather than as a colour change. And every meaning gets
/// exactly one hue at one weight — the marks below all sit in the same lightness
/// band, which is what makes a row of them look like a set instead of a
/// collection.
///
/// The XAML half of this is <c>UI/Theme.xaml</c>, and the two must agree.
/// </summary>
public static class Tone
{
    // ---------- surfaces: one hue, evenly spaced, deepest first ----------

    /// <summary>Inset: text boxes and anything you type into.</summary>
    public const string Well = "#070B12";

    /// <summary>Recessed: the project rail and the timeline pane.</summary>
    public const string Rail = "#0A0F1A";

    /// <summary>One step under the rail: a group heading sitting on it.</summary>
    public const string Sunk = "#0C121D";

    /// <summary>The window itself.</summary>
    public const string Page = "#0F172A";

    /// <summary>The header and footer bars. Spelled "Head" in Theme.xaml too.</summary>
    public const string Head = "#131C2E";

    /// <summary>A list row: raised off the page, below a card.</summary>
    public const string Row = "#18202F";

    /// <summary>Cards, and the selected row.</summary>
    public const string Card = "#1E293B";

    /// <summary>Hover, and the quiet buttons.</summary>
    public const string Lift = "#273449";

    // ---------- lines ----------

    /// <summary>A divider inside a dark pane, meant to be barely there.</summary>
    public const string Hair = "#1B2436";

    /// <summary>An ordinary border.</summary>
    public const string Line = "#334155";

    /// <summary>A border under the pointer.</summary>
    public const string Edge = "#475569";

    // ---------- text ----------

    public const string Ink = "#E2E8F0";
    public const string Ink2 = "#CBD5E1";
    public const string Faint = "#94A3B8";
    public const string Dim = "#64748B";

    /// <summary>
    /// Selection and keyboard focus, and nothing else.
    ///
    /// It used to double as the colour of a deploy branch, which made the row
    /// you had clicked and the row that happens to be production look like the
    /// same statement. One job each.
    /// </summary>
    public const string Pick = "#3B82F6";

    /// <summary>A chip or row that is currently the chosen filter.</summary>
    public const string PickTint = "#1D3A66";

    // ---------- meanings ----------
    //
    // Three values each: the mark (a stripe, a dot, a heading), the tint behind
    // a pill, and the ink on that tint. Every mark is the 400-weight of its hue,
    // so no one meaning shouts louder than another by accident.

    /// <summary>A deploy branch: the reference, not a thing being measured.</summary>
    public const string Deploy = "#818CF8";
    public const string DeployTint = "#232A5E";
    public const string DeployInk = "#C7D2FE";

    /// <summary>A whole row that is a deploy branch: the tint, not the pill.</summary>
    public const string DeployRow = "#1C2440";

    /// <summary>Not shipped anywhere yet. Neutral: it is not a fault.</summary>
    public const string Open = "#94A3B8";
    public const string OpenTint = "#26324A";
    public const string OpenInk = "#CBD5E1";

    /// <summary>In some environments, not all.</summary>
    public const string Partly = "#FBBF24";
    public const string PartlyTint = "#3A2A0E";
    public const string PartlyInk = "#FDE68A";

    /// <summary>
    /// Couldn't be established — which is not the same as no, and now has its
    /// own hue to say so. It shared amber with "partly shipped" before, so the
    /// two were indistinguishable in the one place they sit side by side.
    /// </summary>
    public const string Unsure = "#FB923C";
    public const string UnsureTint = "#3A230F";
    public const string UnsureInk = "#FED7AA";

    /// <summary>All the way in, everywhere.</summary>
    public const string Shipped = "#4ADE80";
    public const string ShippedTint = "#14301F";
    public const string ShippedInk = "#BBF7D0";

    /// <summary>Shipped everywhere, local only, remote gone: the tidy-up.</summary>
    public const string Tidy = "#2DD4BF";
    public const string TidyTint = "#0F2E2C";
    public const string TidyInk = "#99F6E4";

    /// <summary>Something is wrong.</summary>
    public const string Bad = "#F87171";
    public const string BadTint = "#2C1517";
    public const string BadInk = "#FECACA";
    public const string BadEdge = "#8B2A2A";

    /// <summary>Something needs care, but the reading below it is still true.</summary>
    public const string Warn = "#FBBF24";
    public const string WarnTint = "#33240E";
    public const string WarnInk = "#FDE68A";
    public const string WarnEdge = "#B45309";

    // ---------- the workspace's own states ----------
    //
    // Same rule as the meanings above: every one of these is the 400-weight of
    // its hue. The set used to mix 400s and 500s, which is why some dots looked
    // urgent and others washed out with no relation to how urgent they were.

    public const string Cpu = "#38BDF8";
    public const string Mem = "#A78BFA";

    public const string Live = "#4ADE80";
    public const string Starting = "#38BDF8";
    public const string Soon = "#FBBF24";
    public const string Asking = "#F472B6";
    public const string Stalled = "#FB923C";
    public const string Loaded = "#FACC15";
    public const string Leftover = "#2DD4BF";

    /// <summary>Can't be reached — its own hue, so it isn't read as "stalled".</summary>
    public const string Gone = "#FB7185";

    public const string Denied = "#A78BFA";

    /// <summary>
    /// What colour a workspace state is, decided once.
    ///
    /// The mascot's ring and the dashboard's pip report the same state, and each
    /// used to carry its own copy of this table. They agreed exactly as long as
    /// nobody edited one of them — which lasted until the palette was levelled,
    /// at which point the mascot went on showing the old shades of the states the
    /// dashboard had already restyled. One table, so that cannot happen again.
    /// </summary>
    public static string For(MascotState state) => state switch
    {
        MascotState.Connected => Live,
        MascotState.Starting => Starting,
        MascotState.AutoStopSoon => Soon,
        MascotState.NeedsConfirmation => Asking,
        MascotState.SessionStalled => Stalled,
        MascotState.ResourcesHigh => Loaded,
        MascotState.Leftovers => Leftover,
        MascotState.Unreachable => Gone,
        MascotState.Unauthorized => Denied,
        MascotState.AgentLost => Bad,
        MascotState.WorkspaceDown => Bad,
        _ => Dim,
    };
}
