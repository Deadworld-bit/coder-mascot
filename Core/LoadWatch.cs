namespace CoderMascot.Core;

/// <summary>One reading of how hard the machine is working.</summary>
public readonly record struct LoadSample(double CpuPercent, double MemoryPercent);

/// <summary>
/// Decides when sustained load is worth interrupting someone about.
///
/// Pure, and in Core, because the interesting failures here are all judgement
/// rather than mechanics: crying wolf during a build, or flapping on and off
/// once a minute at the threshold. Neither throws; both just make the warning
/// worthless, and you only find out by living with it for a day.
/// </summary>
public sealed class LoadWatch
{
    private readonly double _cpuLimit;
    private readonly double _memoryLimit;
    private readonly double _sustainSeconds;

    /// <summary>When the current run of over-threshold readings began.</summary>
    private double? _overSince;

    /// <summary>True once we've actually complained, so clearing can be stickier.</summary>
    private bool _warning;

    /// <summary>
    /// How far a reading has to fall back before the warning clears.
    ///
    /// Without this gap, a machine sitting at exactly the threshold toggles the
    /// warning on and off every poll — which is both useless and the most
    /// annoying possible behaviour for a thing that pops up a speech bubble.
    /// </summary>
    private const double ClearMargin = 8;

    public LoadWatch(double cpuLimit, double memoryLimit, double sustainSeconds)
    {
        _cpuLimit = cpuLimit;
        _memoryLimit = memoryLimit;
        _sustainSeconds = Math.Max(0, sustainSeconds);
    }

    /// <summary>Which of the two is over, for the message. Empty when neither is.</summary>
    public bool CpuOver { get; private set; }
    public bool MemoryOver { get; private set; }

    /// <summary>
    /// Feed one reading. Returns true while the machine should be reported as
    /// loaded.
    ///
    /// `now` is seconds from any fixed origin — the caller's clock, so this
    /// stays testable without one.
    /// </summary>
    public bool Update(LoadSample s, double now)
    {
        // Below the clear line on both counts: it's over, whatever it was.
        var clearCpu = s.CpuPercent < _cpuLimit - ClearMargin;
        var clearMemory = s.MemoryPercent < _memoryLimit - ClearMargin;

        if (clearCpu && clearMemory)
        {
            _overSince = null;
            _warning = false;
            CpuOver = MemoryOver = false;
            return false;
        }

        var overCpu = s.CpuPercent >= _cpuLimit;
        var overMemory = s.MemoryPercent >= _memoryLimit;

        if (!overCpu && !overMemory)
        {
            // In the gap between the two lines. Hold whatever we were saying
            // rather than restarting the clock — this is the flapping zone.
            return _warning;
        }

        CpuOver = overCpu;
        MemoryOver = overMemory;
        _overSince ??= now;

        // A build pegs every core for a minute and that is not a problem worth
        // a notification. Only a load that *stays* up is one.
        if (now - _overSince.Value >= _sustainSeconds) _warning = true;

        return _warning;
    }
}

/// <summary>A process family and what it is costing, for the "who" in the warning.</summary>
public readonly record struct LoadCulprit(string Name, int Count, double MemoryGb);

public static class LoadReport
{
    /// <summary>
    /// Turn the numbers into the sentence.
    ///
    /// Naming the culprit is the entire point. "Memory is at 91%" tells you
    /// something you could have seen yourself; "Code.exe ×7 — 9.4 GB" tells you
    /// what to close, which is the thing you actually forgot.
    /// </summary>
    public static string Describe(LoadSample s, bool cpuOver, bool memoryOver,
                                  IReadOnlyList<LoadCulprit> culprits)
    {
        var what = (cpuOver, memoryOver) switch
        {
            (true, true) => $"CPU {s.CpuPercent:F0}% and memory {s.MemoryPercent:F0}%",
            (true, false) => $"CPU has been at {s.CpuPercent:F0}%",
            _ => $"Memory is at {s.MemoryPercent:F0}%",
        };

        var top = culprits
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Take(3)
            .Select(c => c.Count > 1
                ? $"{c.Name} ×{c.Count} ({c.MemoryGb:F1} GB)"
                : $"{c.Name} ({c.MemoryGb:F1} GB)")
            .ToArray();

        return top.Length == 0 ? $"{what}." : $"{what} — {string.Join(", ", top)}.";
    }

    /// <summary>
    /// Group raw per-process readings into families, biggest first.
    ///
    /// By name, because the thing being forgotten is never one process: a
    /// single VS Code window is a helper, a renderer, a language server and an
    /// extension host, and listing those separately buries the fact that there
    /// are seven windows open. Counting *windows* is not possible from here, so
    /// this counts processes and lets the number speak for itself.
    /// </summary>
    public static List<LoadCulprit> Group(IEnumerable<(string Name, long Bytes)> processes,
                                          double minGb = 0.5)
    {
        return processes
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LoadCulprit(g.Key, g.Count(), g.Sum(p => p.Bytes) / 1024.0 / 1024 / 1024))
            .Where(c => c.MemoryGb >= minGb)
            .OrderByDescending(c => c.MemoryGb)
            .ToList();
    }
}
