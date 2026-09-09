using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CoderMascot.Core;

/// <summary>What the local machine is doing, as reported to the mascot.</summary>
public sealed record LoadSnapshot
{
    public bool Loaded { get; init; }
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public string Detail { get; init; } = string.Empty;

    public static readonly LoadSnapshot Idle = new();

    public bool SameAs(LoadSnapshot o) => Loaded == o.Loaded && Detail == o.Detail;
}

/// <summary>
/// Watches the CPU and memory of the machine the mascot is running on — not the
/// workspace. The habit this is for is local: editors and dev servers left open
/// across a dozen projects, each cheap on its own.
///
/// Deliberately not PerformanceCounter. That pulls in the perf-counter service,
/// takes a second or more to prime, and returns 0 or throws often enough on a
/// stripped Windows install to be a liability in something that must never be
/// the reason the mascot stops reporting. GetSystemTimes and
/// GlobalMemoryStatusEx are two kernel calls with no setup and no failure mode
/// beyond a false return.
/// </summary>
public sealed class SystemMonitor : IAsyncDisposable
{
    private readonly CoderConfig _cfg;
    private readonly CancellationTokenSource _cts = new();
    private LoadWatch _watch;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task? _loop;

    private ulong _lastIdle, _lastKernel, _lastUser;
    private bool _primed;

    public event EventHandler<LoadSnapshot>? Changed;

    public LoadSnapshot Latest { get; private set; } = LoadSnapshot.Idle;

    public SystemMonitor(CoderConfig cfg)
    {
        _cfg = cfg;
        _watch = new LoadWatch(cfg.CpuWarnPercent, cfg.MemoryWarnPercent, cfg.LoadSustainSeconds);
    }

    private TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(_cfg.LoadPollSeconds, 2, 600));

    public void Start()
    {
        if (!_cfg.WatchResources) return;
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Sample();
            }
            catch (Exception ex)
            {
                // A machine-load reading is the least important thing here. It
                // must never take the loop down with it.
                Debug.WriteLine($"[CoderMascot] load sample failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Sample()
    {
        var cpu = ReadCpuPercent();
        var mem = ReadMemoryPercent();

        // CPU needs two readings to mean anything, so the first poll only
        // establishes the baseline.
        if (!_primed) return;

        var sample = new LoadSample(cpu, mem);
        var loaded = _watch.Update(sample, _clock.Elapsed.TotalSeconds);

        var snap = new LoadSnapshot
        {
            Loaded = loaded,
            // Carried on every reading, warning or not: the dashboard draws the
            // live numbers, and a gauge that only has a value while something is
            // wrong is a gauge that reads zero whenever you look at it.
            CpuPercent = cpu,
            MemoryPercent = mem,

            // Only enumerate processes when we're actually about to complain.
            // Walking every process on the machine every ten seconds to say
            // nothing would make the monitor the thing worth complaining about.
            Detail = loaded
                ? LoadReport.Describe(sample, _watch.CpuOver, _watch.MemoryOver, Culprits())
                : string.Empty,
        };

        var news = !snap.SameAs(Latest);
        Latest = snap;
        if (!news) return;

        try { Changed?.Invoke(this, snap); }
        catch { /* a broken listener must not stop the watch */ }
    }

    /// <summary>
    /// Pick up changed thresholds without a restart.
    ///
    /// The watch carries the sustained-load state machine, so rebuilding it also
    /// resets the timer — which is the honest behaviour after moving the line
    /// the timer is measuring against.
    /// </summary>
    public void Reconfigure() =>
        _watch = new LoadWatch(_cfg.CpuWarnPercent, _cfg.MemoryWarnPercent, _cfg.LoadSustainSeconds);

    /// <summary>
    /// The biggest memory families on this machine, grouped by process name.
    /// Public so the dashboard can ask on demand — it is far too expensive to
    /// put on the sampling loop.
    /// </summary>
    public static List<LoadCulprit> Culprits()
    {
        var readings = new List<(string, long)>();

        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    readings.Add((p.ProcessName, p.WorkingSet64));
                }
                catch
                {
                    // Exited between the enumeration and the read, or a
                    // protected process. Neither is worth a word.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CoderMascot] process enumeration failed: {ex.Message}");
        }

        return LoadReport.Group(readings);
    }

    // ---------- kernel readings ----------

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        var i = ToUlong(idle);
        var k = ToUlong(kernel);
        var u = ToUlong(user);

        if (!_primed)
        {
            (_lastIdle, _lastKernel, _lastUser) = (i, k, u);
            _primed = true;
            return 0;
        }

        // Kernel time *includes* idle time, so total is kernel + user and the
        // busy fraction is 1 - idle/total. Subtracting idle from kernel first
        // is the classic way to get this subtly wrong.
        var dIdle = i - _lastIdle;
        var dTotal = (k - _lastKernel) + (u - _lastUser);

        (_lastIdle, _lastKernel, _lastUser) = (i, k, u);

        if (dTotal == 0) return 0;
        return Math.Clamp(100.0 * (1.0 - (double)dIdle / dTotal), 0, 100);
    }

    private static double ReadMemoryPercent()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : 0;
    }

    private static ulong ToUlong(FILETIME t) => ((ulong)(uint)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

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
