using System.Diagnostics;

namespace CoderMascot.Core;

/// <summary>
/// Polls each watched repository's CI and turns the readings into two things: the
/// deploy branches that are currently red, and the pipelines that finished since
/// the last look.
///
/// A slow loop of its own — talking to two APIs is not something to do on the
/// workspace cadence — and it keeps the last reading per repository so it can
/// tell a run that just finished from one that was already done. The read itself
/// is handed in, so this class never touches a token or an HttpClient; it only
/// decides what the readings mean, using <see cref="CiWatch"/>.
/// </summary>
public sealed class CiMonitor : IAsyncDisposable
{
    private readonly CoderConfig _cfg;
    private readonly Func<GitProject, CancellationToken, Task<CiSnapshot?>> _read;
    private readonly Dictionary<string, CiSnapshot> _last = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private IReadOnlyList<(GitProject Project, CiSnapshot Reading)> _readings = [];
    private Task? _loop;

    /// <summary>The deploy branches currently red, refreshed every sweep.</summary>
    public event EventHandler<IReadOnlyList<BrokenDeploy>>? BrokenChanged;

    /// <summary>Pipelines that finished since the last look and pass the announce policy.</summary>
    public event EventHandler<IReadOnlyList<PipelineNews>>? Finished;

    /// <summary>A sweep completed — the window may repaint its pane.</summary>
    public event EventHandler? Refreshed;

    public CiMonitor(CoderConfig cfg, Func<GitProject, CancellationToken, Task<CiSnapshot?>> read)
    {
        _cfg = cfg;
        _read = read;
    }

    /// <summary>The latest reading of every repository, for the window.</summary>
    public IReadOnlyList<(GitProject Project, CiSnapshot Reading)> Readings
    {
        get { lock (_gate) return _readings; }
    }

    public void Start()
    {
        if (_cfg.CiPollSeconds <= 0 || _loop is not null) return;

        _loop = Task.Run(() => LoopAsync(_cts.Token));

        // The loop guards each project and the delay, so it should only ever end
        // on cancellation; if it faults anyway, that must not vanish silently.
        _loop.ContinueWith(t => CrashLog.Write("CiMonitor loop", t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var readings = new List<(GitProject, CiSnapshot)>();
            var news = new List<PipelineNews>();

            foreach (var entry in _cfg.Projects)
            {
                if (ct.IsCancellationRequested) return;

                var project = entry.ToProject();
                if (project.Ci == CiKind.Off) continue;

                CiSnapshot? snap;
                try
                {
                    snap = await _read(project, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CiMonitor] {project.Name}: {ex.Message}");
                    continue;
                }

                if (snap is null) continue;   // no CI resolvable for this repository

                readings.Add((project, snap));

                try
                {
                    if (_last.TryGetValue(project.Id, out var previous))
                    {
                        foreach (var pipeline in CiWatch.Finished(previous, snap))
                            if (CiWatch.Announce(_cfg.AnnouncePipelines, pipeline))
                                news.Add(new PipelineNews(project.Name, pipeline));
                    }

                    // Keep only a good reading as the baseline: a failed or
                    // not-signed-in one carries no pipelines, so diffing the next
                    // good sweep against it would miss what finished during the
                    // blip and re-announce everything after a sign-in.
                    if (snap.Problem is null && !snap.NeedsSignIn) _last[project.Id] = snap;
                }
                catch (Exception ex)
                {
                    // One unreadable diff skips this project, never the sweep.
                    Debug.WriteLine($"[CiMonitor] {project.Name} diff: {ex.Message}");
                }
            }

            lock (_gate) _readings = readings;

            Raise(() => BrokenChanged?.Invoke(this, CiWatch.Broken(readings)));
            if (news.Count > 0) Raise(() => Finished?.Invoke(this, news));
            Raise(() => Refreshed?.Invoke(this, EventArgs.Empty));

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_cfg.CiPollSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static void Raise(Action fire)
    {
        try { fire(); }
        catch { /* a broken listener must never stop the poll */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* shutting down anyway */ }
        }

        _cts.Dispose();
    }
}
