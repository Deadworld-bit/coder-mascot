namespace CoderMascot.Core;

/// <summary>How loudly the mascot is allowed to interrupt.</summary>
public enum AlertPolicy
{
    /// <summary>Toasts, speech bubbles, reminders, and stopping to be noticed.</summary>
    All,

    /// <summary>
    /// No words. The badge still changes colour and the mascot still stops, so
    /// the information is all still there — it just doesn't talk.
    /// </summary>
    Quiet,

    /// <summary>Nothing at all. Decoration, plus a tray icon that changes colour.</summary>
    Off,
}

/// <summary>
/// Decides what the mascot is allowed to say, and remembers what you've already
/// been told.
///
/// Two separate ideas, deliberately kept apart:
///
///   * The **policy** is standing — "stop talking to me", set once.
///   * An **acknowledgement** is per-problem — "yes, I know about *this* one",
///     and it expires the moment the problem changes into a different one.
///
/// Acknowledging must never make a live problem *look* solved. The badge and the
/// tray colour are the persistent truth and are not routed through here at all;
/// what this gate controls is the interrupting — the toast, the bubble, the
/// repeat nudges, and the mascot parking itself in a corner to be noticed. A
/// dead workspace that you have acknowledged is still a red badge; it has just
/// stopped shouting.
/// </summary>
public sealed class AlertGate
{
    /// <summary>The problem the user has said they already know about.</summary>
    private MascotState? _acknowledged;

    public AlertPolicy Policy { get; set; } = AlertPolicy.All;

    /// <summary>"Yes, I know" — about whatever is on screen right now.</summary>
    public void Acknowledge(MascotState state)
    {
        // Acknowledging good news is meaningless, and recording it would then
        // swallow the next real problem if it happened to arrive as the same
        // state we were told to ignore.
        if (state.NeedsAttention()) _acknowledged = state;
    }

    /// <summary>
    /// Called on every reading. A different problem clears the previous
    /// acknowledgement, so each new thing gets one full chance to interrupt.
    /// </summary>
    public void Observe(MascotState state)
    {
        if (_acknowledged is { } ack && ack != state) _acknowledged = null;
    }

    public bool IsAcknowledged(MascotState state) => _acknowledged == state;

    /// <summary>Is the mascot allowed to say anything about this state?</summary>
    private bool Speaks(MascotState state) =>
        Policy == AlertPolicy.All && !IsAcknowledged(state);

    public bool AllowToast(MascotState state) => Speaks(state);

    public bool AllowBubble(MascotState state) => Speaks(state);

    public bool AllowReminder(MascotState state) => Speaks(state) && state.NeedsAttention();

    /// <summary>
    /// Should the mascot stop patrolling and go to a corner?
    ///
    /// Survives Quiet on purpose: with the words turned off, movement is the
    /// only thing left that can catch your eye, so silencing the text must not
    /// silence that too. It does *not* survive an acknowledgement — a mascot
    /// parked in a corner is itself an interruption, holding a screen corner
    /// and refusing to get on with it, which is most of what there is to be
    /// annoyed by once you already know.
    /// </summary>
    public bool AllowPark(MascotState state) =>
        Policy != AlertPolicy.Off && state.NeedsAttention() && !IsAcknowledged(state);

    public static AlertPolicy Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "quiet" => AlertPolicy.Quiet,
        "off" or "none" => AlertPolicy.Off,
        _ => AlertPolicy.All,
    };

    public static string Text(AlertPolicy p) => p switch
    {
        AlertPolicy.Quiet => "quiet",
        AlertPolicy.Off => "off",
        _ => "all",
    };
}
