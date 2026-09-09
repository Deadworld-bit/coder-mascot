namespace CoderMascot.Core;

/// <summary>
/// What the mascot is currently telling you.
///
/// The numbers are stable identifiers, NOT a severity ranking — the session
/// states were added last and sit above WorkspaceDown despite being less
/// severe. Precedence lives in App.Combine and the IsAlarm/NeedsAttention
/// predicates; never compare these ordinals.
/// </summary>
public enum MascotState
{
    /// <summary>Nothing polled yet.</summary>
    Unknown = 0,

    /// <summary>Workspace running, agent connected and ready.</summary>
    Connected = 1,

    /// <summary>Running, but the agent is still coming up (or not ready yet).</summary>
    Starting = 2,

    /// <summary>Running and healthy, but Coder will auto-stop it soon.</summary>
    AutoStopSoon = 3,

    /// <summary>We can't reach the Coder deployment at all — network or DNS.</summary>
    Unreachable = 4,

    /// <summary>The session token is missing, expired, or rejected.</summary>
    Unauthorized = 5,

    /// <summary>Workspace is running but the agent dropped. Sessions are dead.</summary>
    AgentLost = 6,

    /// <summary>Workspace itself is stopped, failed, or being deleted.</summary>
    WorkspaceDown = 7,

    /// <summary>A Claude session is sitting at a permission prompt, waiting for you.</summary>
    NeedsConfirmation = 8,

    /// <summary>A Claude session says it's running but hasn't done anything in a long time.</summary>
    SessionStalled = 9,

    /// <summary>This machine has been pinned at high CPU or memory for a while.</summary>
    ResourcesHigh = 10,

    /// <summary>An idle workspace, or dev servers left listening. Nothing is wrong.</summary>
    Leftovers = 11,
}

public static class MascotStateInfo
{
    /// <summary>True when this state means your Claude sessions are not usable.</summary>
    public static bool IsAlarm(this MascotState s) =>
        s is MascotState.Unreachable or MascotState.Unauthorized
          or MascotState.AgentLost or MascotState.WorkspaceDown;

    /// <summary>
    /// True when the mascot should stop patrolling and come get you. Wider than
    /// IsAlarm: the auto-stop countdown is still "healthy", but it's the one
    /// warning you can actually act on before it costs you a session.
    /// </summary>
    public static bool NeedsAttention(this MascotState s) =>
        s.IsAlarm()
        || s is MascotState.AutoStopSoon
             or MascotState.NeedsConfirmation
             or MascotState.SessionStalled
             or MascotState.ResourcesHigh;

    public static string Title(this MascotState s) => s switch
    {
        MascotState.Connected => "Workspace connected",
        MascotState.Starting => "Workspace starting",
        MascotState.AutoStopSoon => "Auto-stop coming up",
        MascotState.NeedsConfirmation => "Claude needs you",
        MascotState.SessionStalled => "Session looks stuck",
        MascotState.ResourcesHigh => "This machine is straining",
        MascotState.Leftovers => "Still running",
        MascotState.Unreachable => "Can't reach Coder",
        MascotState.Unauthorized => "Session token rejected",
        MascotState.AgentLost => "Workspace agent lost",
        MascotState.WorkspaceDown => "Workspace is down",
        _ => "Checking…",
    };
}
