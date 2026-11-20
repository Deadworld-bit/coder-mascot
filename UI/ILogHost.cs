using CoderMascot.Core;

namespace CoderMascot.UI;

/// <summary>What the Logs page reads and edits: the configured sources and what was read from them.</summary>
public interface ILogHost
{
    /// <summary>The sources, in the order the user listed them.</summary>
    IReadOnlyList<LogSourceEntry> Sources { get; }

    /// <summary>The reader. Its <see cref="LogMonitor.Refreshed"/> fires off the UI thread.</summary>
    LogMonitor Reader { get; }

    /// <summary>
    /// Replace the sources through the config's sieve, save, and read again at once.
    /// Returns what was kept, which may differ from what was handed in — or null when
    /// the config file couldn't be written, so the change won't outlive this run.
    /// </summary>
    IReadOnlyList<LogSourceEntry>? SaveSources(IReadOnlyList<LogSourceEntry> entries);

    /// <summary>A connection string sealed for the config file (DPAPI, this Windows account).</summary>
    string Seal(string connection);

    /// <summary>"server / database" from a plain connection string, for display; null when it won't parse.</summary>
    string? HostOf(LogSourceEntry draft, string connection);

    /// <summary>
    /// For the form: connect with <paramref name="connection"/> (or, when null, the draft's
    /// saved one), list the tables, and read the chosen table's columns and a few rows.
    /// Never throws for a bad connection: the probe says what went wrong, in safe words.
    /// May block briefly before its first await (a mongodb+srv name is resolved), so call it off the UI thread.
    /// </summary>
    Task<TableProbe> ProbeAsync(LogSourceEntry draft, string? connection, CancellationToken ct);
}
