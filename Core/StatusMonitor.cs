namespace CoderMascot.Core;

public sealed class StateChangedEventArgs(WorkspaceSnapshot snapshot, bool shouldNotify) : EventArgs
{
    public WorkspaceSnapshot Snapshot { get; } = snapshot;

    /// <summary>True when this transition deserves a toast, not just a repaint.</summary>
    public bool ShouldNotify { get; } = shouldNotify;
}

/// <summary>
/// Polls Coder on a timer and decides when a reading is worth reacting to.
///
/// Two behaviours matter here:
///
/// * Asymmetric debouncing — an alarm needs N consecutive *alarm* polls (of any
///   kind) before it fires, so one dropped packet can't cry wolf, while recovery
///   applies immediately so going green is never delayed.
///
/// * The loop is structurally unkillable. A monitor that dies silently would
///   leave a green, smiling mascot on screen while the workspace is gone — the
///   single worst failure this app can have — so nothing except cancellation is
///   allowed to exit the loop, and a watchdog catches it if that ever fails.
/// </summary>
public sealed class StatusMonitor : IAsyncDisposable
{
    private readonly CoderConfig _cfg;
    private readonly CoderClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly AsyncAutoResetEvent _wake = new();
    private readonly TimeSpan _interval;
    private Task? _loop;

    private MascotState _committed = MascotState.Unknown;
    private WorkspaceSnapshot? _committedSnapshot;
    private MascotState _pending = MascotState.Unknown;
    private int _alarmStreak;
    private bool _stallReported;

    public event EventHandler<StateChangedEventArgs>? Changed;

    public WorkspaceSnapshot? Latest { get; private set; }

    /// <summary>When a poll last completed, successfully or not.</summary>
    public DateTimeOffset? LastPollUtc { get; private set; }

    public StatusMonitor(CoderConfig cfg)
    {
        _cfg = cfg;
        _client = new CoderClient(cfg);
        _interval = TimeSpan.FromSeconds(Math.Clamp(cfg.PollSeconds, 5, 3600));
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>Poll right now instead of waiting for the next tick.</summary>
    /// <summary>New credentials: drop what was resolved and look again now.</summary>
    public void Reauthenticate()
    {
        _client.Forget();
        PollNow();
    }

    public void PollNow() => _wake.Set();

    /// <summary>
    /// Start or stop the workspace, then poll immediately so the badge reflects
    /// the build rather than sitting on the old colour for another 20 seconds.
    /// </summary>
    public async Task<string?> TransitionAsync(string workspaceId, string transition)
    {
        var error = await _client.TransitionAsync(workspaceId, transition, _cts.Token)
                                 .ConfigureAwait(false);
        if (error is null) PollNow();
        return error;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = await _client.PollAsync(ct).ConfigureAwait(false);
                Apply(snap);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Apply(new WorkspaceSnapshot
                {
                    State = MascotState.Unreachable,
                    Detail = $"Monitor error: {ex.Message}",
                });
            }

            try
            {
                await _wake.WaitAsync(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A broken delay must not end the loop; fall through and re-poll.
            }
        }
    }

    /// <summary>
    /// Called from a UI-thread timer. If the loop has stopped producing polls
    /// for any reason, say so instead of leaving a stale green mascot up.
    /// </summary>
    public void CheckForStall()
    {
        if (LastPollUtc is not { } last) return;

        var stalled = DateTimeOffset.UtcNow - last > _interval + _interval + _interval;

        if (!stalled)
        {
            _stallReported = false;
            return;
        }

        if (_stallReported) return;
        _stallReported = true;

        Raise(new WorkspaceSnapshot
        {
            State = MascotState.Unreachable,
            Detail = "Monitor stopped responding — restart Coder Mascot.",
            WorkspaceName = Latest?.WorkspaceName,
        }, notify: true);
    }

    private void Apply(WorkspaceSnapshot snap)
    {
        LastPollUtc = DateTimeOffset.UtcNow;
        _stallReported = false;

        var incoming = snap.State;

        if (incoming == _committed)
        {
            _pending = incoming;
            _alarmStreak = 0;
            Latest = snap;
            _committedSnapshot = snap;

            // Same state, but the detail line moves (auto-stop countdown), so
            // repaint without re-notifying.
            Raise(snap, notify: false);
            return;
        }

        // A missing token or bad URL is settled configuration, not a flaky
        // packet — making the user stare at "Checking…" for 40s helps nobody.
        var needsConfirmation = incoming.IsAlarm()
                                && incoming != MascotState.Unauthorized
                                && _cfg.FailuresBeforeAlarm > 1;

        if (needsConfirmation)
        {
            // Count consecutive ALARM polls, not consecutive identical ones.
            // A flapping link alternating Unreachable/AgentLost is still a
            // continuous outage; counting identical states would reset the
            // streak every poll and suppress the alarm forever.
            _alarmStreak = _pending.IsAlarm() ? _alarmStreak + 1 : 1;
            _pending = incoming;

            if (_alarmStreak < _cfg.FailuresBeforeAlarm)
            {
                // Not confirmed yet — keep showing the last committed reading
                // verbatim. Pairing the old state with the new detail would
                // render "Workspace connected — agent disconnected".
                Raise(_committedSnapshot ?? snap with { State = _committed, Detail = _committed.Title() },
                      notify: false);
                return;
            }
        }

        var previous = _committed;
        _committed = incoming;
        _committedSnapshot = snap;
        _pending = incoming;
        _alarmStreak = 0;
        Latest = snap;

        // Don't toast the very first reading if everything is fine — nobody wants
        // a notification at login saying "all good".
        var notify = !(previous == MascotState.Unknown
                       && !incoming.IsAlarm()
                       && incoming != MascotState.AutoStopSoon);

        Raise(snap, notify);
    }

    /// <summary>A subscriber that throws must not take the monitor down with it.</summary>
    private void Raise(WorkspaceSnapshot snap, bool notify)
    {
        try
        {
            Changed?.Invoke(this, new StateChangedEventArgs(snap, notify));
        }
        catch
        {
            // Nothing useful to do — and stopping the loop would be far worse.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _wake.Set();

        if (_loop is not null)
        {
            // ConfigureAwait(false) is load-bearing: OnExit blocks the UI thread
            // waiting on this, so capturing the dispatcher context here would
            // deadlock until the timeout.
            try { await _loop.ConfigureAwait(false); } catch { /* shutting down */ }
        }

        _cts.Dispose();
        _client.Dispose();
    }
}

/// <summary>Minimal awaitable auto-reset event, so PollNow can interrupt the sleep.</summary>
internal sealed class AsyncAutoResetEvent
{
    private readonly SemaphoreSlim _sem = new(0, 1);

    public void Set()
    {
        try { if (_sem.CurrentCount == 0) _sem.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        _sem.WaitAsync(timeout, ct);
}
