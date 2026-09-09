namespace CoderMascot.Core;

/// <summary>
/// Which animation plays on which screen edge, and which way it faces.
///
/// This lives in Core, away from WPF, for one reason: it is testable here. The
/// failure it guards against is not a crash — it is a mascot that climbs the
/// right-hand wall facing into it, or plays the floor animation while hanging
/// off the ceiling. Nothing throws, nothing logs, and the only way to notice is
/// to watch it happen on a screen none of this is built on.
/// </summary>
public static class Pose
{
    /// <summary>
    /// Is the mascot travelling *upwards* along this edge?
    ///
    /// `dir` is +1 clockwise around the desktop from the top-left corner, which
    /// means it points down the right-hand wall and up the left-hand one. Getting
    /// this backwards flies the character up the screen playing a dive.
    /// </summary>
    public static bool GoingUp(Edge e, int dir) => e switch
    {
        Edge.Left => dir > 0,
        Edge.Right => dir < 0,
        _ => false,
    };

    /// <summary>The animation for an edge, moving or stopped.</summary>
    public static string Animation(MovementStyle style, Edge e, bool moving, int dir = 1) =>
        style == MovementStyle.Flies
            ? e switch
            {
                // Grounded when it settles on the floor, airborne everywhere else:
                // a creature that flies has nothing to stand on up a wall.
                Edge.Bottom => moving ? "fly" : "idle",
                Edge.Top => moving ? "fly" : "flyidle",
                _ => moving ? (GoingUp(e, dir) ? "ascend" : "descend") : "hover",
            }
            : e switch
            {
                Edge.Bottom => moving ? "run" : "idle",
                Edge.Top => "hang",                            // swinging along the ceiling
                _ => moving ? "climb" : "climbidle",           // scaling a wall, or clinging to it
            };

    /// <summary>
    /// Whether to mirror the sprite, given the travel direction (+1 clockwise
    /// around the desktop, -1 anticlockwise).
    ///
    /// The two styles disagree about the walls, and the disagreement is the
    /// point. A climber's back is against the wall, so its facing is set by
    /// *which wall* — mirroring by direction would turn it to face into the
    /// bricks every time it changed its mind. A flier is in open air facing the
    /// way it's going, so its facing follows direction on every edge, walls
    /// included.
    /// </summary>
    public static bool Mirrored(MovementStyle style, Edge e, int dir) => e switch
    {
        Edge.Top => dir < 0,
        Edge.Bottom => dir > 0,
        _ when style == MovementStyle.Flies => dir > 0,
        Edge.Right => true,      // climber, mirrored so its back is to the right-hand wall
        _ => false,
    };

    /// <summary>
    /// Every animation a style can ask for. The sprite folders must cover these
    /// exactly — an animation with no frames falls back silently, and the mascot
    /// spends the rest of the session in the wrong pose.
    /// </summary>
    public static string[] All(MovementStyle style) =>
        style == MovementStyle.Flies
            ? ["idle", "fly", "flyidle", "hover", "ascend", "descend"]
            : ["idle", "run", "hang", "climb", "climbidle"];
}
