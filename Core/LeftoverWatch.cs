namespace CoderMascot.Core;

/// <summary>Everything currently running that you probably meant to stop.</summary>
public sealed record LeftoverReport
{
    /// <summary>How long the workspace has been untouched, if it's idle enough to mention.</summary>
    public TimeSpan? WorkspaceIdle { get; init; }

    public IReadOnlyList<DevServer> Servers { get; init; } = [];

    public bool Any => WorkspaceIdle is not null || Servers.Count > 0;

    /// <summary>Human-readable line for the bubble and the toast.</summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// The "you left it on" watch.
///
/// One state covers two very different things — a Coder workspace nobody has
/// touched in an hour, and dev servers on this laptop from a project you closed
/// this morning — because to the person looking at it they are the same fact:
/// something is running that shouldn't be. The dashboard splits them apart again
/// where the actions differ, which is the right place for that distinction.
///
/// This is deliberately not <see cref="MascotState.ResourcesHigh"/>. High load
/// is *costing you speed right now* and wants you to act; a leftover is costing
/// you quota and tidiness and can wait until you look. Folding them together
/// would mean either nagging about the quiet case or staying silent about the
/// urgent one.
/// </summary>
public static class LeftoverWatch
{
    /// <summary>
    /// Has the workspace gone quiet for long enough to be worth mentioning?
    ///
    /// The signal is Coder's own <c>last_used_at</c> — the same field its
    /// auto-stop is driven by — rather than anything the mascot infers. Guessing
    /// from Claude sessions alone would call the workspace idle while you were
    /// happily working in code-server, and there is no faster way to make a
    /// warning worthless than to have it be wrong while you are looking at it.
    /// </summary>
    public static TimeSpan? WorkspaceIdleFor(DateTimeOffset? lastUsed, DateTimeOffset now, int afterMinutes)
    {
        if (afterMinutes <= 0 || lastUsed is not { } used) return null;

        var quiet = now - used;

        // A clock skewed the other way would otherwise read as a huge idle time
        // and stop a workspace somebody is using.
        if (quiet < TimeSpan.Zero) return null;

        return quiet >= TimeSpan.FromMinutes(afterMinutes) ? quiet : null;
    }

    public static LeftoverReport Build(TimeSpan? workspaceIdle, IReadOnlyList<DevServer> stale)
    {
        var parts = new List<string>(2);

        if (workspaceIdle is { } idle)
            parts.Add($"Workspace untouched for {Friendly.Duration(idle.TotalSeconds)}.");

        if (stale.Count > 0)
            parts.Add(DevServers.Describe(stale));

        return new LeftoverReport
        {
            WorkspaceIdle = workspaceIdle,
            Servers = stale,
            Detail = string.Join(" ", parts),
        };
    }
}
