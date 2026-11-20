using System.Diagnostics;

namespace CoderMascot.Core;

/// <summary>
/// Keeps the watched repositories current: fetches each one on a slow timer and
/// says so only when a deploy branch has moved — a merge reaching an environment.
///
/// A background fetch, so the branch and merge readings are fresh when you open
/// the window, and a merge to production announces itself without you looking.
/// The first reading of each repository is a baseline that announces nothing, and
/// a reading that failed is not evidence anything moved — both live in
/// <see cref="BranchDiff"/>. Its own slow loop rather than a passenger on another
/// monitor: a fetch talks to a server and is minutes of work on a bad link, which
/// has no business on a ten-second cadence.
/// </summary>
public sealed class RepoMonitor : IAsyncDisposable
{
    private readonly CoderConfig _cfg;
    private readonly Func<GitProject, CancellationToken, Task<ProjectSnapshot>> _read;
    private readonly Dictionary<string, ProjectSnapshot> _last = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>A ready-to-show line about a deploy branch that moved.</summary>
    public event EventHandler<string>? DeployMoved;

    /// <summary>A sweep finished and the readings changed — the window may repaint.</summary>
    public event EventHandler? Refreshed;

    public RepoMonitor(CoderConfig cfg, Func<GitProject, CancellationToken, Task<ProjectSnapshot>> read)
    {
        _cfg = cfg;
        _read = read;
    }

    /// <summary>The most recent reading of each repository, for the window.</summary>
    public ProjectSnapshot? Latest(string projectId) =>
        _last.TryGetValue(projectId, out var snap) ? snap : null;

    public void Start()
    {
        if (_cfg.AutoFetchMinutes <= 0 || _loop is not null) return;

        _loop = Task.Run(() => LoopAsync(_cts.Token));

        // Guarded per project and at the delay, so it should only end on
        // cancellation; a fault must still not disappear without a trace.
        _loop.ContinueWith(t => CrashLog.Write("RepoMonitor loop", t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var changed = false;

            foreach (var entry in _cfg.Projects)
            {
                if (ct.IsCancellationRequested) return;

                var project = entry.ToProject();

                ProjectSnapshot snap;
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
                    // One unreadable repository must never stop the sweep of the rest.
                    Debug.WriteLine($"[RepoMonitor] {project.Name}: {ex.Message}");
                    continue;
                }

                try
                {
                    if (_last.TryGetValue(project.Id, out var previous) && _cfg.AnnounceDeploys)
                    {
                        var news = BranchDiff.DeployNews(project.Name, BranchDiff.Between(previous, snap), project.Deployed);
                        if (news is not null) Raise(() => DeployMoved?.Invoke(this, news));
                    }

                    // Keep only a good reading as the baseline, so a merge that
                    // lands during a failed sweep is still news on the next one.
                    if (snap.Problem is null) _last[project.Id] = snap;
                }
                catch (Exception ex)
                {
                    // One unreadable diff skips this project, never the sweep.
                    Debug.WriteLine($"[RepoMonitor] {project.Name} diff: {ex.Message}");
                }

                changed = true;
            }

            if (changed) Raise(() => Refreshed?.Invoke(this, EventArgs.Empty));

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_cfg.AutoFetchMinutes), ct).ConfigureAwait(false);
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
        catch { /* a broken listener must never stop the sweep */ }
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
