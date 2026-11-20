namespace CoderMascot.Core;

public enum LogSeverity { Unknown, Trace, Debug, Info, Warning, Error, Fatal }

/// <summary>One log entry, whatever it was read from.</summary>
public sealed class LogEntry
{
    /// <summary>When it was written, or null when the entry carries no readable time.</summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary>
    /// What the entry sorts by: its own time, or else the nearest time around it in
    /// the same file, so an untimed line stays next to its neighbours when files
    /// are merged.
    /// </summary>
    public DateTimeOffset SortTime { get; init; }

    public LogSeverity Level { get; init; }

    /// <summary>One line: the first line without its time and level, or a JSON entry's rendered message.</summary>
    public string Message { get; init; } = "";

    /// <summary>Continuation lines, or a JSON entry's exception and properties; null when there are none.</summary>
    public string? Detail { get; init; }

    /// <summary>The first line exactly as written; for a JSON entry that is the whole entry.</summary>
    public string FirstLine { get; init; } = "";

    public bool Structured { get; init; }

    /// <summary>The continuation was longer than the cap and was cut.</summary>
    public bool Cut { get; init; }

    /// <summary>The file it came from, relative to the source folder.</summary>
    public string Source { get; init; } = "";

    /// <summary>The line it starts on, counted from the start of what was read.</summary>
    public long Line { get; init; }

    /// <summary>The entry as written (LF line breaks), for the clipboard.</summary>
    public string Text => Structured || Detail is null ? FirstLine : FirstLine + "\n" + Detail;
}

/// <summary>One file's reading.</summary>
public sealed record LogFileRead(
    string Name,
    string Path,
    long Length,
    DateTime WrittenUtc,
    IReadOnlyList<LogEntry> Entries,
    int Dropped,
    long SkippedBytes,
    string? Problem,
    bool OverBudget = false);

/// <summary>Everything a source holds right now: a folder's files and entries, or a table's entries.</summary>
public sealed record LogFolderView(
    LogSourceEntry Source,
    IReadOnlyList<LogFileRead> Files,
    IReadOnlyList<LogEntry> Entries,
    string? Problem,
    DateTime ReadUtc)
{
    public int Errors { get; } = Entries.Count(e => e.Level >= LogSeverity.Error);
    public int Warnings { get; } = Entries.Count(e => e.Level == LogSeverity.Warning);

    /// <summary>The oldest entries the budget left out of <see cref="Entries"/>.</summary>
    public int Trimmed { get; init; }

    /// <summary>A table whose last read failed: why, while <see cref="Entries"/> still shows what was read before.</summary>
    public string? Stale { get; init; }

    /// <summary>Something the last read did that is worth saying: the table started again, or rows were skipped.</summary>
    public string? Note { get; init; }

    public bool IsTable => LogSourceKinds.IsTable(Source.Kind);
}
