namespace CoderMascot.Core;

/// <summary>
/// One Claude session, as the workspace reported it.
///
/// The counts on <see cref="SessionSnapshot"/> are what the badge needs; this is
/// what a person needs — which folder, doing what, for how long. It exists so
/// the dashboard can answer "which of my four sessions is the stuck one" without
/// alt-tabbing through four terminals.
/// </summary>
public sealed record SessionLine
{
    public required string Id { get; init; }

    /// <summary>waiting | running | stalled | idle, straight from the summary.</summary>
    public required string Status { get; init; }

    /// <summary>thinking | tool | null — only meaningful while running.</summary>
    public string? Phase { get; init; }

    /// <summary>The tool in flight, when there is one.</summary>
    public string? Tool { get; init; }

    /// <summary>Last path component of the session's working directory.</summary>
    public string? Folder { get; init; }

    /// <summary>Claude's own prompt text, when it's waiting on you.</summary>
    public string? Message { get; init; }

    /// <summary>Seconds since this session last did anything.</summary>
    public int IdleFor { get; init; }

    /// <summary>How long the most recent turn took, once the session goes idle.</summary>
    public int RanFor { get; init; }

    public bool IsBusy => Status is "running" or "waiting" or "stalled";

    /// <summary>One line describing what this session is doing right now.</summary>
    public string Activity => Status switch
    {
        "waiting" => string.IsNullOrWhiteSpace(Message) ? "Waiting for you" : Message!,
        "stalled" => Phase == "thinking"
            ? "Thinking, but not moving"
            : Tool is { Length: > 0 } t ? $"{t} — no sign of finishing" : "Running, but not moving",
        "running" => Phase == "thinking"
            ? "Thinking…"
            : Tool is { Length: > 0 } t2 ? $"{t2}…" : "Working…",
        _ => RanFor > 0 ? $"Finished — took {Friendly.Duration(RanFor)}" : "Idle",
    };
}

/// <summary>
/// Which sessions finished since the last reading.
///
/// This is the notification the app was missing. It only ever told you when
/// something needed you or had gone wrong — but the whole shape of working with
/// an agent is that you start something and go and do something else, and
/// nothing was telling you it was done.
///
/// Edge-triggered against the previous poll rather than read from a flag, so a
/// session that stays idle for an hour is announced once, not every 30 seconds.
/// </summary>
public static class SessionDiff
{
    /// <summary>Sessions that were busy last time we looked and are idle now.</summary>
    public static IReadOnlyList<SessionLine> JustFinished(
        IReadOnlyDictionary<string, string> before, IReadOnlyList<SessionLine> now)
    {
        // No previous reading at all means the app just started. Announcing
        // every already-idle session as "just finished" on launch would be a
        // burst of notifications about work that finished before it was running.
        if (before.Count == 0) return [];

        var done = new List<SessionLine>();
        foreach (var s in now)
        {
            if (s.Status != "idle") continue;
            if (before.TryGetValue(s.Id, out var was) && was is "running" or "waiting" or "stalled")
                done.Add(s);
        }
        return done;
    }

    public static Dictionary<string, string> StatusById(IReadOnlyList<SessionLine> sessions)
    {
        var map = new Dictionary<string, string>(sessions.Count, StringComparer.Ordinal);
        foreach (var s in sessions) map[s.Id] = s.Status;
        return map;
    }

    /// <summary>The notification text for a batch of finishes.</summary>
    public static string Describe(IReadOnlyList<SessionLine> finished)
    {
        if (finished.Count == 0) return string.Empty;

        if (finished.Count == 1)
        {
            var s = finished[0];
            var where = string.IsNullOrWhiteSpace(s.Folder) ? "Claude" : $"Claude in {s.Folder}";
            return s.RanFor > 0
                ? $"{where} finished — {Friendly.Duration(s.RanFor)}."
                : $"{where} finished.";
        }

        return $"{finished.Count} Claude sessions finished.";
    }
}

/// <summary>Shared human-scale formatting. Everything here is for reading, not parsing.</summary>
public static class Friendly
{
    /// <summary>
    /// How long ago something happened, in the coarsest unit that stays honest.
    /// "3d ago" is what you want to know about a merge; the exact minute is not.
    /// </summary>
    public static string Since(DateTimeOffset at)
    {
        var seconds = (DateTimeOffset.Now - at).TotalSeconds;
        return seconds < 45 ? "just now" : $"{Duration(seconds)} ago";
    }

    /// <summary>A duration in seconds, at the coarsest unit that stays honest.</summary>
    public static string Duration(double seconds) => seconds switch
    {
        < 60 => $"{Math.Max(1, (int)seconds)}s",
        < 3600 => $"{(int)(seconds / 60)} min",
        < 86400 => $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m",
        _ => $"{(int)(seconds / 86400)}d",
    };
}
