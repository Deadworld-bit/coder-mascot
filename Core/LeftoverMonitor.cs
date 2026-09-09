using System.Diagnostics;

namespace CoderMascot.Core;

/// <summary>
/// Sweeps the machine for dev servers nobody is talking to any more.
///
/// Its own loop rather than a passenger on <see cref="SystemMonitor"/>, and a
/// much slower one: a leftover is by definition something that has already been
/// running for hours, so checking every ten seconds would buy nothing and put a
/// process-table walk on a hot path. The two watches also answer opposite
/// questions — one is "is this machine struggling right now", the other "is
/// there something here I finished with" — and giving them one interval would
/// mean tuning one of them wrong.
/// </summary>
public sealed class LeftoverMonitor : IAsyncDisposable
{
    private readonly CoderConfig _cfg;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public event EventHandler? Changed;

    /// <summary>Everything currently listening that looks self-started.</summary>
    public IReadOnlyList<DevServer> Servers { get; private set; } = [];

    /// <summary>The subset old enough to count as forgotten.</summary>
    public IReadOnlyList<DevServer> Stale =>
        DevServers.Stale(Servers, _cfg.DevServerIdleMinutes);

    public LeftoverMonitor(CoderConfig cfg) => _cfg = cfg;

    public void Start()
    {
        if (!_cfg.WatchLeftovers || _cfg.DevServerIdleMinutes <= 0) return;
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Re-scan now — after closing something, so the row disappears.</summary>
    public void ScanNow()
    {
        try
        {
            Apply(PortScanner.Scan());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CoderMascot] leftover scan failed: {ex.Message}");
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ScanNow();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Apply(IReadOnlyList<DevServer> found)
    {
        // Compare on identity, not uptime. Uptime changes on every scan, so
        // comparing the whole record would raise a change event every minute and
        // re-render the dashboard for nothing.
        var before = Key(Stale);
        Servers = found;

        if (Key(Stale) == before) return;

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A broken listener must never stop the sweep.
        }
    }

    private static string Key(IReadOnlyList<DevServer> servers) =>
        string.Join(",", servers.Select(s => $"{s.Pid}:{s.Port}"));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Shutting down anyway.
            }
        }

        _cts.Dispose();
    }
}
