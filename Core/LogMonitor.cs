using System.IO;
using System.Security;

namespace CoderMascot.Core;

/// <summary>
/// Keeps every configured log source read: folders and database tables, each on a
/// loop of its own, so a database that takes its time answering never holds up a
/// folder you just pasted into.
///
/// Folders are polled rather than watched: each sweep lists the folder and re-reads
/// only the files whose length, write time or creation time changed. A
/// FileSystemWatcher drops events when its buffer overflows, stops for good when the
/// folder is deleted and made again, and a paste fires a burst of events anyway — a
/// listing every few seconds is cheaper than handling all of that.
///
/// Tables are read incrementally: the newest rows once, then only the rows after the
/// last one read, at the table poll interval. What the Logs page lists of a table is
/// not that: it is one page at a time, read from the server when asked (<see cref="PageAsync"/>).
/// </summary>
public sealed class LogMonitor : IAsyncDisposable
{
    /// <summary>The newest rows read when a table is first opened, and per batch after that.</summary>
    internal const int InitialRows = 1000;
    internal const int BatchRows = 500;
    private const int MaxBatches = 4;
    public const long MaxTableChars = 8L * 1024 * 1024;

    /// <summary>All tables together; with many tables, each holds less than <see cref="MaxTableChars"/>.</summary>
    public const long MaxTotalTableChars = 32L * 1024 * 1024;

    private const int ParallelReads = 4;
    private static readonly TimeSpan TableReadLimit = TimeSpan.FromSeconds(60);

    /// <summary>The least time between two reads of a table that the user asked for (Reload).</summary>
    internal static readonly TimeSpan AskedGap = TimeSpan.FromSeconds(15);

    private readonly Func<IReadOnlyList<LogSourceEntry>> _sources;
    private readonly Func<int> _pollSeconds;
    private readonly Func<int> _tablePollSeconds;
    private readonly Func<LogSourceEntry, ILogTableReader>? _openTable;
    private readonly long _folderBytes;
    private readonly Dictionary<string, Cached> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TableState> _tables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The page readers, one per table the page has asked for and apart from the poll's own:
    /// a poll that fails closes its reader, which must not cut off a page being read.
    /// </summary>
    private readonly Dictionary<string, Pager> _pagers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A page reader not used for this long is closed: a MongoDB client keeps connections and heartbeats open.</summary>
    private static readonly TimeSpan PagerIdle = TimeSpan.FromMinutes(5);

    private bool _disposed;

    private sealed class Pager(LogSourceEntry source, ILogTableReader reader)
    {
        public LogSourceEntry Source { get; } = source;
        public ILogTableReader Reader { get; } = reader;
        public DateTime UsedUtc = DateTime.UtcNow;
    }
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _tableWake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    // Each loop owns its own half and swaps in a fresh dictionary; Publish puts the
    // two together in the configured order.
    private IReadOnlyDictionary<string, LogFolderView> _folderViews = new Dictionary<string, LogFolderView>();
    private IReadOnlyDictionary<string, LogFolderView> _tableViews = new Dictionary<string, LogFolderView>();
    private IReadOnlyList<LogFolderView> _views = [];
    private Task? _loop;
    private Task? _tableLoop;

    /// <summary>One table source's running state. Touched only by the table loop, one read at a time per table.</summary>
    private sealed class TableState(LogSourceEntry source)
    {
        public LogSourceEntry Source { get; set; } = source;
        public ILogTableReader? Reader;
        public readonly LinkedList<(string Key, LogEntry Entry)> Held = new();
        public readonly HashSet<string> Keys = new(StringComparer.Ordinal);
        public long Chars;
        public int Dropped;
        public string? Cursor;
        public long? LastNumber;
        public string[]? WindowKeys;
        public bool Window;
        public int Failures;
        public DateTime DueUtc = DateTime.MinValue;
        public DateTime? LastReadUtc;
        public LogFolderView? View;
    }

    private readonly record struct Stamp(long Length, DateTime WrittenUtc, DateTime CreatedUtc);

    private sealed record Cached(Stamp Stamp, LogFileRead Read);

    /// <summary>Something changed in at least one source. Raised off the UI thread.</summary>
    public event EventHandler? Refreshed;

    /// <param name="openTable">Opens a reader for a table source, or throws <see cref="LogTableException"/> saying why it can't.</param>
    public LogMonitor(Func<IReadOnlyList<LogSourceEntry>> sources, Func<int> pollSeconds,
                      long folderBytes = LogFile.MaxFolderBytes,
                      Func<LogSourceEntry, ILogTableReader>? openTable = null, Func<int>? tablePollSeconds = null)
    {
        _sources = sources;
        _pollSeconds = pollSeconds;
        _folderBytes = folderBytes;
        _openTable = openTable;
        _tablePollSeconds = tablePollSeconds ?? (() => 300);
    }

    /// <summary>One view per configured source, in the configured order.</summary>
    public IReadOnlyList<LogFolderView> Views
    {
        get { lock (_gate) return _views; }
    }

    public LogFolderView? View(string sourceId) =>
        Views.FirstOrDefault(v => string.Equals(v.Source.Id, sourceId, StringComparison.OrdinalIgnoreCase));

    public void Start()
    {
        if (_loop is not null) return;

        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _loop.ContinueWith(t => Report("loop", t.Exception?.GetBaseException() ?? new InvalidOperationException()),
            TaskContinuationOptions.OnlyOnFaulted);

        _tableLoop = Task.Run(() => TableLoopAsync(_cts.Token));
        _tableLoop.ContinueWith(t => Report("table loop", t.Exception?.GetBaseException() ?? new InvalidOperationException()),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// List the folders now instead of at the next poll. A table is read early only when it
    /// is named — the user pressed Reload on it — and not twice within <see cref="AskedGap"/>:
    /// showing the page or saving a folder must not send a production database a query.
    /// </summary>
    public void ReadNow(string? tableId = null)
    {
        Wake(_wake);
        if (tableId is null) return;

        lock (_asked) _asked.Add(tableId);
        Wake(_tableWake);
    }

    private static void Wake(SemaphoreSlim wake)
    {
        try
        {
            if (wake.CurrentCount == 0) wake.Release();
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Sweep(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad sweep must not end the reading for good.
                Report("sweep", ex);
            }

            try
            {
                var wait = TimeSpan.FromSeconds(Math.Clamp(_pollSeconds(), 2, 3600));
                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Read every folder once. True when what the page shows changed.</summary>
    internal bool Sweep(CancellationToken ct = default)
    {
        var previous = _folderViews;
        var sources = _sources();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var views = new List<LogFolderView>(sources.Count);
        var chars = LogFile.MaxTotalChars;

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (source.Kind != LogSourceKinds.Folder || string.IsNullOrEmpty(source.Path)) continue;

            var before = previous.GetValueOrDefault(source.Id);

            LogFolderView view;
            try
            {
                view = ReadFolder(source, before, seen, ref chars, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // One folder that can't be read must not freeze the others at their last reading.
                Report($"folder {views.Count + 1}", ex);
                const string problem = "Something in this folder couldn't be read.";
                view = before is { Problem: problem } && before.Source.SameAs(source)
                    ? before
                    : new LogFolderView(source, [], [], problem, DateTime.UtcNow);
            }

            views.Add(view);
        }

        // Files no source reaches any more, or that left their folder.
        foreach (var path in _files.Keys.Where(k => !seen.Contains(k)).ToList()) _files.Remove(path);

        var fresh = new Dictionary<string, LogFolderView>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in views) fresh.TryAdd(view.Source.Id, view);
        lock (_gate) _folderViews = fresh;

        return Publish();
    }

    /// <summary>
    /// Put both halves together in the configured order and tell the page — only when
    /// a view is a different object from last time, which is how "nothing changed" is said.
    /// </summary>
    private bool Publish()
    {
        lock (_gate)
        {
            var sources = _sources();
            var list = new List<LogFolderView>(sources.Count);

            foreach (var source in sources)
            {
                var views = LogSourceKinds.IsTable(source.Kind) ? _tableViews : _folderViews;
                if (views.TryGetValue(source.Id, out var view)) list.Add(view);
            }

            if (list.Count == _views.Count && list.Zip(_views).All(p => ReferenceEquals(p.First, p.Second))) return false;
            _views = list;
        }

        try
        {
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A broken listener must never stop the reading.
        }

        return true;
    }

    private async Task TableLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string[] asked;
            lock (_asked)
            {
                asked = [.. _asked];
                _asked.Clear();
            }

            try
            {
                await SweepTablesAsync(force: false, DateTime.UtcNow, ct, asked).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Report("table sweep", ex);
            }

            try
            {
                await _tableWake.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Read every table that is due — all of them when <paramref name="force"/>, and those in
    /// <paramref name="asked"/> when they were last read at least <see cref="AskedGap"/> ago.
    /// True when what the page shows changed.
    /// </summary>
    internal async Task<bool> SweepTablesAsync(bool force, DateTime nowUtc, CancellationToken ct = default,
                                               IReadOnlyCollection<string>? asked = null)
    {
        var sources = _sources().Where(s => LogSourceKinds.IsTable(s.Kind)).ToList();
        var changed = false;

        await PrunePagersAsync(sources, nowUtc).ConfigureAwait(false);

        foreach (var (id, state) in _tables.ToList())
        {
            var now = sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

            // A new name is only a new label: the reading goes on where it was.
            if (now is not null && now.ReadsSameAs(state.Source))
            {
                if (!now.SameAs(state.Source))
                {
                    state.Source = now;
                    if (state.View is { } view) state.View = view with { Source = now };
                    changed = true;
                }

                continue;
            }

            // Gone, or reading something else now (another connection, other columns): start again.
            _tables.Remove(id);
            await Close(state).ConfigureAwait(false);
            changed = true;
        }

        var poll = TimeSpan.FromSeconds(Math.Clamp(_tablePollSeconds(), 15, 3600));
        var due = new List<TableState>();

        foreach (var source in sources)
        {
            if (!_tables.TryGetValue(source.Id, out var state))
            {
                state = new TableState(source);
                _tables[source.Id] = state;
            }

            var wanted = asked?.Contains(source.Id, StringComparer.OrdinalIgnoreCase) == true
                         && (state.LastReadUtc is not { } last || nowUtc - last >= AskedGap);

            if (force || wanted || nowUtc >= state.DueUtc) due.Add(state);
        }

        // Few at a time, so one server that doesn't answer holds up only its own table.
        var cap = Math.Min(MaxTableChars, MaxTotalTableChars / Math.Max(1, sources.Count));
        using var gate = new SemaphoreSlim(ParallelReads);

        var reads = due.Select(async state =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var before = state.View;
                await ReadTableAsync(state, nowUtc, poll, cap, ct).ConfigureAwait(false);
                return !ReferenceEquals(before, state.View);
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var moved in await Task.WhenAll(reads).ConfigureAwait(false)) changed |= moved;

        if (changed)
        {
            var fresh = new Dictionary<string, LogFolderView>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, state) in _tables)
                if (state.View is { } view) fresh[id] = view;

            lock (_gate) _tableViews = fresh;
        }

        return Publish() || changed;
    }

    private async Task ReadTableAsync(TableState state, DateTime nowUtc, TimeSpan poll, long cap, CancellationToken ct)
    {
        string? failure = null;
        string? note = null;
        var moved = false;
        state.LastReadUtc = nowUtc;

        try
        {
            state.Reader ??= _openTable?.Invoke(state.Source)
                             ?? throw new LogTableException("Tables can't be read in this build.");

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TableReadLimit);

            if (state.Cursor is null || state.Window)
            {
                var read = await state.Reader.ReadAsync(null, InitialRows, limit.Token).ConfigureAwait(false);
                moved = read.Window ? Replace(state, read.Rows, cap) : Append(state, read.Rows, cap);
                state.Window = read.Window;
            }
            else
            {
                var full = 0;
                for (var batch = 0; batch < MaxBatches; batch++)
                {
                    var read = await state.Reader.ReadAsync(state.Cursor, BatchRows, limit.Token).ConfigureAwait(false);

                    // Nothing after the last id, and the newest id is below it: the table was
                    // emptied or its identity reseeded, and "after the last id" would never match again.
                    if (batch == 0 && read.Rows.Count == 0 && state.LastNumber is { } last)
                    {
                        var newest = await state.Reader.ReadAsync(null, 1, limit.Token).ConfigureAwait(false);
                        if (newest.Rows is [{ Number: { } top }] && top < last)
                        {
                            Forget(state);
                            read = await state.Reader.ReadAsync(null, InitialRows, limit.Token).ConfigureAwait(false);
                            moved = true;
                            note = "The table was emptied or its ids started again; it is being read afresh.";
                        }
                    }

                    moved |= Append(state, read.Rows, cap);
                    if (read.Rows.Count < BatchRows) break;
                    full++;
                }

                // Rows arrive faster than they are read: catch up by skipping to the newest, and say so.
                if (full == MaxBatches)
                {
                    var newest = await state.Reader.ReadAsync(null, InitialRows, limit.Token).ConfigureAwait(false);
                    moved |= Append(state, newest.Rows, cap);
                    note = "Rows arrive faster than they are read here; some were skipped to show the newest.";
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            failure = $"The server didn't answer within {TableReadLimit.TotalSeconds:0} seconds.";
        }
        catch (LogTableException ex)
        {
            failure = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Report("reading a table", ex, $"{state.Source.Id}|{ex.GetType().FullName}");
            failure = "The table couldn't be read.";
        }

        if (failure is null)
        {
            state.Failures = 0;
            state.DueUtc = nowUtc + poll;
        }
        else
        {
            // A server that is struggling is asked less often, not more: double the wait, up to an hour.
            state.Failures++;
            var wait = poll.TotalSeconds * Math.Pow(2, Math.Min(state.Failures - 1, 12));
            state.DueUtc = nowUtc + TimeSpan.FromSeconds(Math.Min(wait, 3600));

            // A reader that failed may hold a table shape that is no longer true (a renamed column).
            await Close(state).ConfigureAwait(false);
        }

        var before = state.View;
        if (failure is null)
        {
            if (!moved && note is null && before is { Problem: null, Stale: null }) return;
            state.View = TableView(state, null, note, nowUtc);
        }
        else if (state.Held.Count == 0)
        {
            if (before is { } b && b.Problem == failure) return;
            state.View = new LogFolderView(state.Source, [], [], failure, nowUtc);
        }
        else
        {
            // What was read stays on the page; the failure is said above it.
            if (before is { } b && b.Stale == failure && !moved) return;
            state.View = TableView(state, failure, note, nowUtc);
        }
    }

    private static LogFolderView TableView(TableState state, string? stale, string? note, DateTime nowUtc) =>
        new(state.Source, [], state.Held.Select(h => h.Entry).ToArray(), null, nowUtc)
        {
            Trimmed = state.Dropped,
            Stale = stale,
            Note = note,
        };

    /// <summary>
    /// Add the rows not held yet, oldest first, keeping within <paramref name="cap"/> as it goes.
    /// A row is made into an entry before the cursor moves past it. True when anything was added.
    /// </summary>
    private bool Append(TableState state, IReadOnlyList<TableRow> rows, long cap)
    {
        var added = false;
        var source = state.Source.Table ?? "";
        var utc = state.Source.Utc == true;

        foreach (var row in rows)
        {
            LogEntry? entry = null;
            if (!state.Keys.Contains(row.Key))
            {
                try
                {
                    entry = LogTableRows.ToEntry(row, source, utc);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // One row that can't be read is passed over, not read again for ever.
                    Report("reading a row", ex, $"{state.Source.Id}|row|{ex.GetType().FullName}");
                }
            }

            if (row.Cursor is not null) state.Cursor = row.Cursor;
            if (row.Number is { } number) state.LastNumber = number;
            if (entry is null || !state.Keys.Add(row.Key)) continue;

            state.Held.AddLast((row.Key, entry));
            state.Chars += LogFile.Size(entry);
            added = true;

            while (state.Held.Count > LogFile.MaxEntries || (state.Chars > cap && state.Held.Count > 1))
            {
                var (key, oldest) = state.Held.First!.Value;
                state.Held.RemoveFirst();
                state.Keys.Remove(key);
                state.Chars -= LogFile.Size(oldest);
                state.Dropped++;
            }
        }

        return added;
    }

    /// <summary>Window mode: the newest rows replace what is held. True when they differ from the last read.</summary>
    private bool Replace(TableState state, IReadOnlyList<TableRow> rows, long cap)
    {
        // Against the last read, not what is held: the size cap may have kept fewer than were read.
        var keys = rows.Select(r => r.Key).ToArray();
        if (state.WindowKeys is { } last && keys.SequenceEqual(last)) return false;

        Forget(state);
        state.WindowKeys = keys;
        Append(state, rows, cap);
        return true;
    }

    private static void Forget(TableState state)
    {
        state.Held.Clear();
        state.Keys.Clear();
        state.Chars = 0;
        state.Dropped = 0;
        state.Cursor = null;
        state.LastNumber = null;
        state.WindowKeys = null;
    }

    private async Task Close(TableState state)
    {
        if (state.Reader is not { } reader) return;
        state.Reader = null;
        await CloseReader(reader).ConfigureAwait(false);
    }

    private async Task CloseReader(ILogTableReader reader)
    {
        try
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report("closing a table", ex);
        }
    }

    /// <summary>
    /// One page of a table for the Logs page, newest first. Read only when the page asks — opening
    /// it, turning a page, a level or a search, Reload — never on the poll. Never throws for the
    /// table: what went wrong comes back in <see cref="TablePageView.Problem"/>, in safe words.
    /// May block briefly before its first await (a mongodb+srv name is resolved), so call it off the UI thread.
    /// </summary>
    public async Task<TablePageView> PageAsync(string sourceId, TablePageQuery query, CancellationToken ct)
    {
        var source = _sources().FirstOrDefault(s =>
            LogSourceKinds.IsTable(s.Kind) && string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (source is null) return TablePageView.Failed("This table isn't in the list any more.");
        if (_disposed) return TablePageView.Failed("The log reader has stopped.");

        ILogTableReader? reader = null;
        string failure;
        var slow = false;

        try
        {
            reader = await PagerAsync(source).ConfigureAwait(false);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TableReadLimit);

            var page = await reader.PageAsync(query, limit.Token).ConfigureAwait(false);
            var entries = new List<LogEntry>(page.Rows.Count);

            foreach (var row in page.Rows)
            {
                try
                {
                    entries.Add(LogTableRows.ToEntry(row, source.Table ?? "", source.Utc == true));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Report("reading a row", ex, $"{source.Id}|row|{ex.GetType().FullName}");
                }
            }

            return new TablePageView(entries, page.Older, page.TiesCut, null);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Asked for something else meanwhile; a driver may say so with an exception of its own.
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException)
        {
            failure = $"The server didn't answer within {TableReadLimit.TotalSeconds:0} seconds.";
            slow = true;
        }
        catch (LogTableException ex)
        {
            failure = ex.Message;
            slow = ex.Slow;
        }
        catch (ObjectDisposedException)
        {
            failure = "This table's settings changed while the page was being read. Try again.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report("reading a page", ex, $"{source.Id}|page|{ex.GetType().FullName}");
            failure = "The page couldn't be read.";
        }

        // A reader that failed may hold a table shape that is no longer true (a renamed column);
        // one whose server was only slow is kept, so the next try doesn't connect and sign in again.
        if (reader is not null && !slow)
        {
            var close = false;
            lock (_pagers)
            {
                if (_pagers.TryGetValue(source.Id, out var held) && ReferenceEquals(held.Reader, reader))
                {
                    _pagers.Remove(source.Id);
                    close = true;
                }
            }

            if (close) await CloseReader(reader).ConfigureAwait(false);
        }

        return TablePageView.Failed(failure, slow);
    }

    private async Task<ILogTableReader> PagerAsync(LogSourceEntry source)
    {
        ILogTableReader? stale = null;
        lock (_pagers)
        {
            if (_pagers.TryGetValue(source.Id, out var held))
            {
                if (held.Source.ReadsSameAs(source))
                {
                    held.UsedUtc = DateTime.UtcNow;
                    return held.Reader;
                }

                _pagers.Remove(source.Id);
                stale = held.Reader;
            }
        }

        if (stale is not null) await CloseReader(stale).ConfigureAwait(false);

        var reader = _openTable?.Invoke(source) ?? throw new LogTableException("Tables can't be read in this build.");
        ILogTableReader? spare = null;

        lock (_pagers)
        {
            if (_disposed)
            {
                spare = reader;
                reader = null;
            }
            else if (_pagers.TryGetValue(source.Id, out var raced) && raced.Source.ReadsSameAs(source))
            {
                // Two pages asked for at once: keep the first reader, close this one.
                spare = reader;
                reader = raced.Reader;
                raced.UsedUtc = DateTime.UtcNow;
            }
            else
            {
                spare = raced?.Reader;
                _pagers[source.Id] = new Pager(source, reader);
            }
        }

        if (spare is not null) await CloseReader(spare).ConfigureAwait(false);
        return reader ?? throw new ObjectDisposedException(nameof(LogMonitor));
    }

    /// <summary>Close the page readers of tables that are gone, now read something else, or haven't been paged for a while.</summary>
    private async Task PrunePagersAsync(IReadOnlyList<LogSourceEntry> tables, DateTime nowUtc)
    {
        List<ILogTableReader> gone = [];
        lock (_pagers)
        {
            foreach (var (id, held) in _pagers.ToList())
            {
                if (nowUtc - held.UsedUtc <= PagerIdle
                    && tables.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) && t.ReadsSameAs(held.Source))) continue;

                _pagers.Remove(id);
                gone.Add(held.Reader);
            }
        }

        foreach (var reader in gone) await CloseReader(reader).ConfigureAwait(false);
    }

    /// <summary>
    /// Into the crash log once per file state, by type and stack only: an exception's
    /// message can quote the line that caused it, and log text never leaves the page.
    /// </summary>
    private void Report(string where, Exception ex, string? once = null)
    {
        if (once is not null)
        {
            lock (_reported)
            {
                if (_reported.Count > 1000) _reported.Clear();
                if (!_reported.Add(once)) return;
            }
        }

        CrashLog.Write($"LogMonitor, {where}: {ex.GetType().FullName}{Environment.NewLine}{ex.StackTrace}", null);
    }

    /// <summary>The folder's view: <paramref name="before"/> itself when nothing in it changed.</summary>
    private LogFolderView ReadFolder(LogSourceEntry source, LogFolderView? before, HashSet<string> seen,
                                     ref long chars, CancellationToken ct)
    {
        var folder = source.Path!;
        List<FileInfo> files;

        try
        {
            if (!Directory.Exists(folder)) return Problem("The folder doesn't exist.");
            files = LogFile.List(folder, source.Patterns);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Problem("The folder couldn't be listed: " + ex.Message);
        }

        var reads = new List<LogFileRead>(files.Count);
        var budget = LogFile.MaxFolderEntries;
        var bytes = _folderBytes;
        var folderChars = LogFile.MaxFolderChars;

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var file = files[i];
            var name = Path.GetRelativePath(folder, file.FullName);

            if (i >= LogFile.MaxFiles || budget <= 0 || bytes <= 0 || folderChars <= 0 || chars <= 0)
            {
                reads.Add(new LogFileRead(name, file.FullName, file.Length, file.LastWriteTimeUtc, [], 0, 0, null, OverBudget: true));
                continue;
            }

            // Per source: two sources over nested folders name the same file differently.
            var key = source.Id + "|" + file.FullName;
            seen.Add(key);

            Stamp stamp;
            try
            {
                // The listing's sizes and times can lag for a file another program is still appending to.
                file.Refresh();
                stamp = new Stamp(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                reads.Add(new LogFileRead(name, file.FullName, 0, DateTime.MinValue, [], 0, 0, "The file has gone."));
                continue;
            }

            _files.TryGetValue(key, out var cached);

            LogFileRead read;
            if (cached is not null && cached.Stamp == stamp)
            {
                read = cached.Read;
            }
            else
            {
                try
                {
                    read = LogFile.Read(file.FullName, name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // Kept under its stamp, so it isn't parsed and reported again every few seconds.
                    Report("reading a file", ex, $"{key}|{stamp}");
                    read = new LogFileRead(name, file.FullName, stamp.Length, stamp.WrittenUtc, [], 0, 0,
                        "Part of this file couldn't be read.");
                    cached = null;
                    _files[key] = new Cached(stamp, read);
                }

                if (read.Problem is null) _files[key] = new Cached(stamp, read);

                // Mid-copy or locked: keep showing the last good reading, and try again next sweep.
                else if (cached is { Read.Problem: null }) read = cached.Read;
            }

            reads.Add(read);
            budget -= read.Entries.Count;
            bytes -= Math.Min(read.Length, LogFile.MaxBytes);

            var held = read.Entries.Sum(LogFile.Size);
            folderChars -= held;
            chars -= held;
        }

        if (before is { Problem: null } && before.Source.SameAs(source) && SameReads(before.Files, reads))
            return before;

        var (entries, trimmed) = Merge(reads);
        return new LogFolderView(source, reads, entries, null, DateTime.UtcNow) { Trimmed = trimmed };

        LogFolderView Problem(string problem) =>
            before is not null && before.Problem == problem && before.Source.SameAs(source)
                ? before
                : new LogFolderView(source, [], [], problem, DateTime.UtcNow);
    }

    private static bool SameReads(IReadOnlyList<LogFileRead> before, List<LogFileRead> now)
    {
        if (before.Count != now.Count) return false;

        for (var i = 0; i < now.Count; i++)
        {
            var (a, b) = (before[i], now[i]);
            if (ReferenceEquals(a, b)) continue;

            var same = a.Name == b.Name && a.Length == b.Length && a.WrittenUtc == b.WrittenUtc
                       && a.OverBudget == b.OverBudget && a.Problem == b.Problem
                       && (a.OverBudget || a.Problem is not null);
            if (!same) return false;
        }

        return true;
    }

    /// <summary>Every file's entries in time order, oldest first, capped at the folder budget, and how many the cap took.</summary>
    private static (LogEntry[] Entries, int Trimmed) Merge(List<LogFileRead> reads)
    {
        var all = new List<(LogEntry Entry, int Rank)>();
        for (var rank = 0; rank < reads.Count; rank++)
            foreach (var entry in reads[rank].Entries)
                all.Add((entry, rank));

        // Files come newest first, so on a tie the higher rank is the older file.
        all.Sort((x, y) =>
        {
            var c = x.Entry.SortTime.CompareTo(y.Entry.SortTime);
            if (c != 0) return c;
            c = y.Rank.CompareTo(x.Rank);
            return c != 0 ? c : x.Entry.Line.CompareTo(y.Entry.Line);
        });

        var trimmed = Math.Max(0, all.Count - LogFile.MaxFolderEntries);
        if (trimmed > 0) all.RemoveRange(0, trimmed);
        return (all.Select(x => x.Entry).ToArray(), trimmed);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        foreach (var loop in new[] { _loop, _tableLoop })
        {
            if (loop is null) continue;
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* shutting down anyway */ }
        }

        // Only once the table loop has stopped touching them.
        if (_tableLoop is null || _tableLoop.IsCompleted)
            foreach (var state in _tables.Values) await Close(state).ConfigureAwait(false);

        List<ILogTableReader> pagers;
        lock (_pagers)
        {
            _disposed = true;
            pagers = _pagers.Values.Select(p => p.Reader).ToList();
            _pagers.Clear();
        }

        foreach (var reader in pagers) await CloseReader(reader).ConfigureAwait(false);

        _cts.Dispose();
    }
}
