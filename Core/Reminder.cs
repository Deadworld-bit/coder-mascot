namespace CoderMascot.Core;

/// <summary>
/// Decides when to nudge again about something already on screen.
///
/// The case this exists for: Claude stops at a permission prompt, the mascot
/// says so once, and the prompt is then missed — a full-screen editor, a second
/// monitor, or simply looking away. One notification at the moment it happens
/// is exactly the wrong shape for "I forgot".
///
/// The hard part is not reminding, it is not becoming noise. A fixed interval
/// is wrong in both directions: fast enough to catch you in the first minute is
/// unbearable by the tenth, and gentle enough for the tenth misses the first.
/// So the gap doubles each time and then stops growing, which front-loads the
/// nudges when they can still help and settles into a slow background pulse
/// when clearly nobody is at the desk.
/// </summary>
public sealed class Reminder
{
    private readonly double _first;
    private readonly double _max;

    private double _since;
    private double _nextAt;
    private int _count;
    private bool _armed;

    /// <param name="firstSeconds">Quiet period after the initial notification.</param>
    /// <param name="maxSeconds">Longest the gap is ever allowed to grow to.</param>
    public Reminder(double firstSeconds, double maxSeconds)
    {
        _first = Math.Max(5, firstSeconds);
        _max = Math.Max(_first, maxSeconds);
    }

    /// <summary>How many times we've nudged about the current thing.</summary>
    public int Count => _count;

    /// <summary>Start (or restart) the clock. Idempotent for the same subject.</summary>
    public void Begin(double now)
    {
        if (_armed) return;
        _armed = true;
        _since = now;
        _nextAt = now + _first;
        _count = 0;
    }

    /// <summary>Nothing needs attention any more.</summary>
    public void Clear()
    {
        _armed = false;
        _count = 0;
    }

    /// <summary>
    /// Should we nudge right now? Advances the schedule when it returns true.
    /// </summary>
    public bool Due(double now)
    {
        if (!_armed || now < _nextAt) return false;

        _count++;

        // Double, then stop. Capping matters: unbounded backoff means the tenth
        // reminder is hours out, which is the same as not reminding at all.
        var gap = Math.Min(_first * Math.Pow(2, _count), _max);
        _nextAt = now + gap;
        return true;
    }

    /// <summary>How long the current thing has been waiting, in whole minutes.</summary>
    public int WaitingMinutes(double now) => _armed ? (int)((now - _since) / 60) : 0;

    /// <summary>
    /// The nudge text. Says how long it has been, because that is the part that
    /// makes someone act — "still waiting" reads as the same notification
    /// again, "waiting 12 minutes" reads as time being wasted.
    /// </summary>
    public string Text(string title, double now)
    {
        var mins = WaitingMinutes(now);
        return mins < 1 ? $"{title} — still waiting" : $"{title} — waiting {mins} min";
    }
}
