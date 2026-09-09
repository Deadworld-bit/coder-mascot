using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CoderMascot.Core;

/// <summary>What the Claude sessions inside the workspace are doing.</summary>
public sealed record SessionSnapshot
{
    /// <summary>False when we couldn't ask at all (CLI missing, workspace down).</summary>
    public bool Ok { get; init; }

    /// <summary>
    /// False when the workspace-side hook has never run.
    ///
    /// This is the difference between "no Claude needs you" and "I have no way
    /// of knowing whether one does". Worth surfacing: the feature is inert until
    /// install-hooks.sh has been run inside the workspace, and a watchdog that
    /// is quiet because it was never wired up looks exactly like one reporting
    /// good news.
    /// </summary>
    public bool Installed { get; init; }

    public int Waiting { get; init; }
    public int Running { get; init; }
    public int Stalled { get; init; }
    public int Idle { get; init; }

    /// <summary>Human-readable line for the speech bubble.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Every session, for the dashboard. The counts above are for the badge.</summary>
    public IReadOnlyList<SessionLine> Sessions { get; init; } = [];

    /// <summary>Sessions that went idle since the previous reading.</summary>
    public IReadOnlyList<SessionLine> JustFinished { get; init; } = [];

    public static readonly SessionSnapshot Unknown = new() { Ok = false, Detail = "not checked yet" };

    /// <summary>Would presenting this look any different from presenting <paramref name="other"/>?</summary>
    public bool SameAs(SessionSnapshot other) =>
        // A finish is news even when the counts happen to land the same way —
        // one session ending as another begins would otherwise be silent.
        JustFinished.Count == 0 &&
        Ok == other.Ok && Installed == other.Installed &&
        Waiting == other.Waiting && Running == other.Running &&
        Stalled == other.Stalled && Idle == other.Idle &&
        Detail == other.Detail;
}

/// <summary>
/// Watches Claude Code sessions running inside the Coder workspace.
///
/// Answers two questions the Coder API cannot: is a session sitting at a
/// permission prompt waiting for you, and is a session claiming to run while
/// actually doing nothing.
///
/// Transport is one `coder ssh … node mascot-summary.js` per poll. That needs no
/// server process alive in the workspace and reuses the CLI login the mascot
/// already depends on, at the cost of a process spawn per poll — hence the
/// deliberately slow default interval.
/// </summary>
public sealed class SessionMonitor : IAsyncDisposable
{
    /// <summary>Characters that would break out of the remote shell command.</summary>
    private static readonly char[] ShellMeta = [' ', ';', '&', '|', '$', '`', '\n', '\r', '"', '\'', '<', '>'];

    private readonly CoderConfig _cfg;
    private readonly Func<MascotState> _workspaceState;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private SessionSnapshot _lastGood = SessionSnapshot.Unknown;
    private int _consecutiveFailures;
    private bool _stallReported;

    public event EventHandler<SessionSnapshot>? Changed;

    public SessionSnapshot Latest { get; private set; } = SessionSnapshot.Unknown;

    /// <summary>When a poll last completed, successfully or not.</summary>
    public DateTimeOffset? LastPollUtc { get; private set; }

    public SessionMonitor(CoderConfig cfg, Func<MascotState> workspaceState)
    {
        _cfg = cfg;
        _workspaceState = workspaceState;
    }

    private TimeSpan Interval =>
        TimeSpan.FromSeconds(Math.Clamp(_cfg.SessionPollSeconds, 10, 3600));

    public void Start()
    {
        if (!_cfg.WatchSessions) return;
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Don't reach into a workspace we already know is unreachable.
                // Every such attempt would hang for the full timeout, and
                // `coder ssh` against a *stopped* workspace can start it —
                // an app that warns about auto-stop must not be the thing that
                // silently undoes it every 30 seconds.
                if (_workspaceState().IsAlarm())
                {
                    Apply(new SessionSnapshot { Ok = false, Detail = "workspace unreachable" },
                          isFailure: true);
                }
                else
                {
                    Apply(await QueryAsync(ct).ConfigureAwait(false), isFailure: false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Apply(new SessionSnapshot { Ok = false, Detail = Trim(ex.Message) }, isFailure: true);
            }

            try
            {
                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Never let the delay end the loop.
            }
        }
    }

    /// <summary>
    /// Called from the UI watchdog. A wedged session loop would otherwise be
    /// invisible — the mascot would keep showing the last thing it knew.
    /// </summary>
    public void CheckForStall()
    {
        if (!_cfg.WatchSessions || LastPollUtc is not { } last) return;

        if (DateTimeOffset.UtcNow - last <= Interval + Interval + Interval)
        {
            _stallReported = false;
            return;
        }

        if (_stallReported) return;
        _stallReported = true;

        Latest = new SessionSnapshot { Ok = false, Detail = "session watch stopped responding" };
        Raise(Latest);
    }

    /// <summary>Status per session id at the previous reading, for the finish diff.</summary>
    private Dictionary<string, string> _lastStatuses = new(StringComparer.Ordinal);

    /// <summary>
    /// Mark the sessions that went idle since last time.
    ///
    /// Only ever from a good reading. A failed `coder ssh` returns no sessions,
    /// and treating that as "they all finished" would announce a completed run
    /// for every session every time the network hiccuped — which is also why the
    /// remembered statuses are left alone rather than cleared.
    /// </summary>
    private SessionSnapshot WithFinishes(SessionSnapshot snap)
    {
        if (!snap.Ok || !snap.Installed) return snap;

        var finished = SessionDiff.JustFinished(_lastStatuses, snap.Sessions);
        _lastStatuses = SessionDiff.StatusById(snap.Sessions);

        return finished.Count == 0 ? snap : snap with { JustFinished = finished };
    }

    private void Apply(SessionSnapshot snap, bool isFailure)
    {
        LastPollUtc = DateTimeOffset.UtcNow;
        _stallReported = false;
        snap = WithFinishes(snap);

        if (isFailure || !snap.Ok)
        {
            // One flaky `coder ssh` must not cancel a live "Claude needs you"
            // and then re-raise it 30s later with a fresh toast.
            if (++_consecutiveFailures < 2 && _lastGood.Ok)
                return;
        }
        else
        {
            _consecutiveFailures = 0;
            _lastGood = snap;
        }

        if (snap.SameAs(Latest))
        {
            // Still take the reading. The dashboard pulls per-session detail on
            // its own timer, and "nothing the badge cares about changed" is not
            // the same as "the elapsed times are still right".
            Latest = snap;
            return;
        }

        Latest = snap;
        Raise(snap);
    }

    private void Raise(SessionSnapshot snap)
    {
        try
        {
            Changed?.Invoke(this, snap);
        }
        catch
        {
            // A broken listener must never stop the watch loop.
        }
    }

    private async Task<SessionSnapshot> QueryAsync(CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(_cfg.Owner) || _cfg.Owner == "me"
            ? _cfg.Workspace
            : $"{_cfg.Owner}/{_cfg.Workspace}";

        if (string.IsNullOrWhiteSpace(target) || target!.StartsWith('-'))
            return new SessionSnapshot { Ok = false, Detail = "no valid workspace configured" };

        // ArgumentList protects the LOCAL exec from argv injection, but
        // `coder ssh <target> -- node <script>` is reassembled into a command
        // string run by the workspace's shell — so the remote path still has to
        // be free of shell metacharacters.
        var script = _cfg.SessionSummaryScript;
        if (string.IsNullOrWhiteSpace(script) || script.IndexOfAny(ShellMeta) >= 0)
            return new SessionSnapshot { Ok = false, Detail = "invalid sessionSummaryScript in config" };

        var cli = CoderCli.Resolve(_cfg);
        if (cli is null)
            return new SessionSnapshot { Ok = false, Detail = "coder CLI not found — set coderCli in config" };

        var psi = new ProcessStartInfo
        {
            FileName = cli,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }.ReadAsUtf8();

        psi.ArgumentList.Add("ssh");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("node");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(Math.Clamp(_cfg.SessionStalledSeconds, 60, 86400).ToString());
        psi.ArgumentList.Add(Math.Clamp(_cfg.SessionThinkingStalledSeconds, 30, 86400).ToString());

        using var proc = new Process { StartInfo = psi };

        try
        {
            if (!proc.Start())
                return new SessionSnapshot { Ok = false, Detail = "could not start the coder CLI" };
        }
        catch (Exception ex)
        {
            return new SessionSnapshot { Ok = false, Detail = $"coder CLI not found ({Trim(ex.Message)})" };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));

        string stdout, stderr;
        try
        {
            // BOTH pipes must be drained before waiting. `coder ssh` writes
            // connection chatter to stderr; leaving it unread fills the 4KB pipe
            // buffer, the child blocks on write and never exits, and every
            // single poll then dies on the 25s timeout.
            var outTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeout.Token);

            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            stdout = await outTask.ConfigureAwait(false);
            stderr = await errTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillQuietly(proc);
            return new SessionSnapshot { Ok = false, Detail = "workspace did not answer in 25s" };
        }
        catch (OperationCanceledException)
        {
            KillQuietly(proc);
            throw;
        }
        catch (Exception ex)
        {
            KillQuietly(proc);
            return new SessionSnapshot { Ok = false, Detail = Trim(ex.Message) };
        }

        if (proc.ExitCode != 0)
        {
            var err = Trim(stderr);
            return new SessionSnapshot
            {
                Ok = false,
                Detail = string.IsNullOrWhiteSpace(err) ? $"coder ssh exited {proc.ExitCode}" : err,
            };
        }

        return Parse(stdout);
    }

    private static SessionSnapshot Parse(string stdout)
    {
        // `coder ssh` can prepend its own chatter, so take the JSON object only.
        var start = stdout.IndexOf('{');
        var end = stdout.LastIndexOf('}');
        if (start < 0 || end <= start)
            return new SessionSnapshot { Ok = false, Detail = "no status returned from the workspace" };

        try
        {
            using var doc = JsonDocument.Parse(stdout[start..(end + 1)]);
            var root = doc.RootElement;

            var installed = !root.TryGetProperty("installed", out var inst) || inst.ValueKind != JsonValueKind.False;
            if (!installed)
                return new SessionSnapshot { Ok = true, Installed = false, Detail = "session hooks not installed" };

            int Count(string name) =>
                root.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

            var waiting = Count("waiting");
            var stalled = Count("stalled");
            var running = Count("running");
            var idle = Count("idle");

            return new SessionSnapshot
            {
                Ok = true,
                Installed = true,
                Waiting = waiting,
                Running = running,
                Stalled = stalled,
                Idle = idle,
                Detail = Describe(waiting, stalled, running, idle, root),
                Sessions = ReadSessions(root),
            };
        }
        catch (JsonException)
        {
            return new SessionSnapshot { Ok = false, Detail = "unreadable status from the workspace" };
        }
    }

    private static string Describe(int waiting, int stalled, int running, int idle, JsonElement root)
    {
        if (waiting > 0)
        {
            // Prefer Claude's own prompt text over a generic count.
            if (root.TryGetProperty("sessions", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in list.EnumerateArray())
                {
                    if (s.TryGetProperty("status", out var st) && st.GetString() == "waiting" &&
                        s.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        var msg = m.GetString();
                        if (!string.IsNullOrWhiteSpace(msg)) return Trim(msg!);
                    }
                }
            }

            return waiting == 1
                ? "A Claude session is waiting for your confirmation."
                : $"{waiting} Claude sessions are waiting for your confirmation.";
        }

        if (stalled > 0)
        {
            // The two stalls want different reactions — one is a model call that
            // may never come back, the other a command that may still be
            // working — so say which, and for how long.
            var s = FirstWith(root, "stalled");
            var which = Text(s, "cwd") is { Length: > 0 } cwd ? $" in {Trim(cwd)}" : "";
            var mins = Number(s, "idleFor") / 60;
            var howLong = mins >= 1 ? $" for {mins} min" : "";

            if (stalled == 1)
            {
                return Text(s, "phase") == "thinking"
                    ? $"A session{which} has been thinking{howLong} without doing anything."
                    : Text(s, "tool") is { Length: > 0 } tool
                        ? $"{Trim(tool)}{which} has been running{howLong} with no sign of finishing."
                        : $"A session{which} says it's running but hasn't moved{howLong}.";
            }

            return $"{stalled} sessions say they're running but haven't moved in a while.";
        }

        return running > 0
            ? $"{running} running, {idle} idle."
            : $"{idle} session(s) idle.";
    }

    /// <summary>
    /// Every session as a row, for the dashboard.
    ///
    /// Ordered by how much they want you: waiting first, then stuck, then
    /// working, then done. The list is short enough that sorting it is cheaper
    /// than making someone scan it.
    /// </summary>
    private static IReadOnlyList<SessionLine> ReadSessions(JsonElement root)
    {
        if (!root.TryGetProperty("sessions", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];

        var rows = new List<SessionLine>(list.GetArrayLength());
        foreach (var s in list.EnumerateArray())
        {
            var id = Text(s, "id");
            if (string.IsNullOrEmpty(id)) continue;

            rows.Add(new SessionLine
            {
                Id = id!,
                Status = Text(s, "status") ?? "idle",
                Phase = Text(s, "phase"),
                Tool = Trim(Text(s, "tool") ?? string.Empty) is { Length: > 0 } t ? t : null,
                Folder = Text(s, "cwd"),
                Message = Trim(Text(s, "message") ?? string.Empty) is { Length: > 0 } m ? m : null,
                IdleFor = Number(s, "idleFor"),
                RanFor = Number(s, "ranFor"),
            });
        }

        var rank = (SessionLine r) => r.Status switch
        {
            "waiting" => 0, "stalled" => 1, "running" => 2, _ => 3,
        };

        return [.. rows.OrderBy(rank).ThenBy(r => r.Folder ?? string.Empty, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The first session in a given status, so its detail can be quoted.</summary>
    private static JsonElement? FirstWith(JsonElement root, string status)
    {
        if (!root.TryGetProperty("sessions", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var s in list.EnumerateArray())
        {
            if (s.TryGetProperty("status", out var st) && st.GetString() == status) return s;
        }

        return null;
    }

    private static string? Text(JsonElement? s, string name) =>
        s is { } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Number(JsonElement? s, string name) =>
        s is { } e && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static void KillQuietly(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static string Trim(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 160 ? s[..160] + "…" : s;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* shutting down */ }
        }
        _cts.Dispose();
    }
}
