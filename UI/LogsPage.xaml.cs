using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CoderMascot.Core;

using Brush = System.Windows.Media.Brush;

namespace CoderMascot.UI;

/// <summary>One source in the left pane. Selection is the row's own `Selected`, as the nav's is.</summary>
public sealed record SourceRow(string Id, string Name, string Path, string Sub, string Tip,
                               bool Selected, Brush Bar, Brush SubInk);

/// <summary>How one level is drawn, decided once for all fifty thousand rows.</summary>
public sealed record LevelLook(string Pill, string Word, Brush Back, Brush Ink, Brush Stripe, Brush Text);

/// <summary>
/// One entry in the list. Immutable and bound once, and the strings are made when
/// a row is first drawn rather than when it is built: of fifty thousand rows,
/// about thirty are ever on screen.
/// </summary>
public sealed class LogRow(LogEntry entry)
{
    /// <summary>A longer message is cut for the list; the detail pane has all of it.</summary>
    private const int PreviewChars = 400;

    private static readonly Brush Clear = System.Windows.Media.Brushes.Transparent;

    // Indexed by LogSeverity, so the order here is the enum's. Error is the coral
    // the broken pipelines use, and warnings the amber; debug and trace sit back.
    private static readonly LevelLook[] Looks =
    [
        new("\u2014", "No level", Clear, Palette.Of(Tone.Dim), Clear, Palette.Of(Tone.Ink2)),
        new("TRC", "Trace", Palette.Of(Tone.Card), Palette.Of(Tone.Faint), Clear, Palette.Of(Tone.Faint)),
        new("DBG", "Debug", Palette.Of(Tone.Card), Palette.Of(Tone.Faint), Clear, Palette.Of(Tone.Faint)),
        new("INF", "Info", Palette.Of(Tone.OpenTint), Palette.Of(Tone.OpenInk), Clear, Palette.Of(Tone.Ink2)),
        new("WRN", "Warning", Palette.Of(Tone.WarnTint), Palette.Of(Tone.WarnInk), Palette.Of(Tone.Warn), Palette.Of(Tone.Ink)),
        new("ERR", "Error", Palette.Of(Tone.BadTint), Palette.Of(Tone.Broken), Palette.Of(Tone.Broken), Palette.Of(Tone.Ink)),
        new("FTL", "Fatal", Palette.Of(Tone.BadEdge), Palette.Of(Tone.BadInk), Palette.Of(Tone.Broken), Palette.Of(Tone.Ink)),
    ];

    private string? _when;

    public LogEntry Entry { get; } = entry;

    public static LevelLook Look(LogSeverity level) =>
        Looks[(int)level is >= 0 and < 7 ? (int)level : 0];

    public string When => _when ??= Entry.Time is { } t
        ? t.ToLocalTime().ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture)
        : "\u2014";

    public string Pill => Look(Entry.Level).Pill;
    public Brush PillBack => Look(Entry.Level).Back;
    public Brush PillInk => Look(Entry.Level).Ink;
    public Brush Stripe => Look(Entry.Level).Stripe;
    public Brush Ink => Look(Entry.Level).Text;

    public string Preview => Entry.Message.Length > PreviewChars ? Entry.Message[..PreviewChars] : Entry.Message;
    public string File => Entry.Source;

    /// <summary>What a screen reader says for the row: when and how bad, never the log's own text.</summary>
    public override string ToString() => $"{When} {Look(Entry.Level).Word}";
}

/// <summary>
/// The log sources, folders and tables, and what is in them: the entries of one source at a time,
/// filtered, with the chosen one shown whole underneath. A folder's entries are all held and
/// filtered here; a table's are one page read from the server, which filters and searches it.
///
/// Everything here is held in memory and drawn here. A log is production data, so
/// no entry's text goes to a bubble, the tray, a toast or the crash log — the one
/// way out is the Copy button, which is the user's own act.
///
/// The reading happens elsewhere and arrives as <see cref="LogMonitor.Refreshed"/>,
/// on a thread that is not this one. The page repaints only while it is on screen,
/// and only the part whose view actually changed: the monitor hands back the same
/// view object for a source where nothing moved.
/// </summary>
public partial class LogsPage : System.Windows.Controls.UserControl
{
    /// <summary>Below this page width the search drops to a line of its own.</summary>
    private const double SearchBesideFrom = 640;

    /// <summary>Below this the pager drops under the status line, which it would otherwise squeeze to nothing.</summary>
    private const double PagerBesideFrom = 760;

    private readonly ILogHost _host;
    private readonly IDrawerHost _drawer;

    /// <summary>
    /// Typing into the search does not filter fifty thousand entries per key; it
    /// waits until the typing pauses.
    /// </summary>
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>The source on the right, by <see cref="LogSourceEntry.Id"/>.</summary>
    private string? _sourceId;

    /// <summary>The folder the list was last built for, and the view it was built from.</summary>
    private string? _paintedId;
    private LogFolderView? _painted;

    private List<SourceRow> _sourceRows = [];
    private List<LogRow> _rows = [];
    private List<string> _fileItems = [];

    /// <summary>The entry in the detail pane. Kept as the entry, not the row: the rows are rebuilt.</summary>
    private LogEntry? _picked;

    /// <summary>The file filter, or null for every file.</summary>
    private string? _file;

    private bool _newestFirst = true;

    /// <summary>Setting a control from code, so its handler doesn't read into it.</summary>
    private bool _quiet;

    /// <summary>A refresh arrived while the page was hidden; paint when it is shown.</summary>
    private bool _dirty = true;

    private bool _stopped;
    private bool _opening;

    /// <summary>A repaint is already queued, so a burst of refreshes is one.</summary>
    private int _queued;

    private bool _wide = true;
    private bool _searchBeside = true;
    private bool _pagerBeside = true;

    private static readonly int[] PageSizes = [50, 100, 200];

    /// <summary>Longer than the reader's own limit, which says what happened in better words.</summary>
    private static readonly TimeSpan PageWait = TimeSpan.FromSeconds(75);

    private enum PageMove { Open, Newest, Newer, Older, Again }

    // A table's page: where it starts, where each page above it starts (nearest last), and the
    // level and search it was read under — what Newer, Older and Reload read again.
    private TablePageView? _page;
    private object? _before;
    private readonly List<object?> _above = [];
    private LogSeverity _floor;
    private string? _search;
    private int _pageSize = 100;

    private CancellationTokenSource? _pageCts;

    /// <summary>The latest page asked for: an answer to an earlier ask is dropped.</summary>
    private int _pageAsk;

    private bool _loading;

    /// <summary>The poll found rows since this page was read, and this page doesn't read them by itself.</summary>
    private bool _newer;

    /// <summary>Reading the page on screen again failed; it stays, with this said above it.</summary>
    private string? _againProblem;

    /// <summary>After the server was slow to answer a page, the next waits this long: a slow search isn't sent again at once.</summary>
    private static readonly TimeSpan PageRest = TimeSpan.FromSeconds(30);

    /// <summary>Reload reads the page on screen again at most this often.</summary>
    private static readonly TimeSpan ReloadGap = TimeSpan.FromSeconds(5);

    private DateTime _restUntil;
    private string? _restFor;
    private DateTime _pageReadAt;

    /// <summary>What the page being read was asked for, so the same search isn't started over by a second Enter.</summary>
    private (LogSeverity Floor, string? Search, int Size)? _asked;

    /// <summary>The poll's newest row when the page last looked: a new view of the same rows (a rename, a failed poll) is not news.</summary>
    private LogEntry? _seenNewest;

    /// <summary>A level or page size picked with the arrow keys passes through the others; only where it stops is read.</summary>
    private readonly DispatcherTimer _pageTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public LogsPage(ILogHost host, IDrawerHost drawer)
    {
        _host = host;
        _drawer = drawer;

        // The combo boxes announce their first selection while the markup is read.
        _quiet = true;
        InitializeComponent();
        LevelBox.SelectedIndex = 0;
        PageSizeBox.SelectedIndex = Array.IndexOf(PageSizes, _pageSize);
        _quiet = false;

        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Refilter();
        };

        _pageTimer.Tick += (_, _) =>
        {
            _pageTimer.Stop();
            LoadPage(PageMove.Open);
        };

        host.Reader.Refreshed += OnRefreshed;

        // Not on Loaded: that fires for a page nobody has opened. Shown means look
        // again at once rather than wait for the next sweep.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue || _stopped) return;

            _host.Reader.ReadNow();
            if (_dirty) Repaint();
        };
    }

    /// <summary>Stop listening. The main window calls this as it closes.</summary>
    public void Stop()
    {
        _stopped = true;
        _searchTimer.Stop();
        _pageTimer.Stop();
        _pageCts?.Cancel();
        _host.Reader.Refreshed -= OnRefreshed;
    }

    // ---------- refresh ----------

    /// <summary>Raised off the UI thread.</summary>
    private void OnRefreshed(object? sender, EventArgs e)
    {
        if (_stopped || Interlocked.Exchange(ref _queued, 1) == 1) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (_stopped) return;

            // Hidden pages are not painted: a log being written to would otherwise
            // rebuild a list of fifty thousand rows behind another page.
            if (!IsVisible) _dirty = true;
            else Repaint();
        });
    }

    private void Repaint()
    {
        _dirty = false;
        PaintSources();
        PaintBody();
    }

    // ---------- the folders ----------

    private LogSourceEntry? Current() =>
        _host.Sources.FirstOrDefault(s => Same(s.Id, _sourceId));

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // A table's id is not a path, so only a folder's falls back to its last segment.
    private static string NameOf(LogSourceEntry source) =>
        source.Name is { Length: > 0 } name ? name
        : IsTable(source) ? (source.Table is { } table ? LogSourceForm.TableLabel(table) : source.Id)
        : source.Id[(source.Id.LastIndexOf('\\') + 1)..];

    private static bool IsTable(LogSourceEntry source) => LogSourceKinds.IsTable(source.Kind);

    private static string Noun(LogSourceEntry source) => source.Kind == LogSourceKinds.Mongo ? "collection" : "table";

    /// <summary>"SQL Server · server / database": the host label is what the config keeps for display, never the connection string.</summary>
    private static string Where(LogSourceEntry source) =>
        $"{(source.Kind == LogSourceKinds.Mongo ? "MongoDB" : "SQL Server")} · {source.Host ?? "server"}";

    private static string Count(int n, string noun, string? plural = null) =>
        $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? noun : plural ?? noun + "s")}";

    private void PaintSources()
    {
        var sources = _host.Sources;

        if (sources.Count == 0) _sourceId = null;
        else if (_sourceId is null || !sources.Any(s => Same(s.Id, _sourceId))) _sourceId = sources[0].Id;

        var rows = new List<SourceRow>(sources.Count);

        foreach (var source in sources)
        {
            var view = _host.Reader.View(source.Id);
            var table = IsTable(source);

            // A table that couldn't be read just now still lists what it has: the line says so in amber.
            var (sub, ink) = view switch
            {
                null => ("Reading\u2026", Tone.Faint),
                { Problem: { } problem } => (problem, Tone.Bad),
                _ when table => ($"{source.Table} · {Count(view.Errors, "error")}", view.Stale is null ? Tone.Faint : Tone.Warn),
                _ => ($"{Count(view.Files.Count, "file")} \u00b7 {Count(view.Errors, "error")}", Tone.Faint),
            };

            var selected = Same(source.Id, _sourceId);

            var path = table ? Where(source) : source.Id;
            var tip = table ? $"{path} · {source.Table}" : source.Id;
            if ((view?.Problem ?? view?.Stale) is { } why) tip = $"{tip}\n{why}";

            rows.Add(new SourceRow(
                source.Id, NameOf(source), path, sub, tip,
                selected,
                selected ? Palette.Of(Tone.Pick) : System.Windows.Media.Brushes.Transparent,
                Palette.Of(ink)));
        }

        // Only when something shows differently: rebuilt on every sweep, the buttons
        // would drop the keyboard focus of whoever had tabbed to one.
        if (rows.SequenceEqual(_sourceRows)) return;

        _sourceRows = rows;
        SourceList.ItemsSource = rows;
    }

    private void OnPickSource(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SourceRow row || Same(row.Id, _sourceId)) return;

        _sourceId = row.Id;
        PaintSources();
        PaintBody();
    }

    // ---------- the folder ----------

    private void PaintBody()
    {
        var source = Current();

        NoSources.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
        Panes.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;

        if (source is null)
        {
            Forget();
            _paintedId = null;
            return;
        }

        if (!Same(_paintedId, source.Id))
        {
            _paintedId = source.Id;
            _file = null;
            _searchTimer.Stop();
            HeadNote.Visibility = Visibility.Collapsed;
            Forget();
        }

        var table = IsTable(source);
        var detail = table ? $"{Where(source)} · {source.Table}" : source.Id;

        HeadTitle.Text = NameOf(source);
        HeadDetail.Text = detail;
        HeadDetail.ToolTip = detail;

        // A table has no folder to open and no files to pick between, and is read a page at a time, newest first.
        BtnOpen.Visibility = FileBox.Visibility = SortButton.Visibility = table ? Visibility.Collapsed : Visibility.Visible;
        PagerBar.Visibility = table ? Visibility.Visible : Visibility.Collapsed;

        Field.SetHint(SearchBox, table ? $"Search this {Noun(source)}, then press Enter  (Ctrl+F)" : "Search messages and details  (Ctrl+F)");
        SearchBox.ToolTip = !table
            ? "Find text in an entry's message or its details  (Ctrl+F, Esc clears)"
            : source.Kind == LogSourceKinds.Mongo
                ? "Find text in the message, exception, properties or logger fields, on the server. Only text values are searched, not sub-documents. Enter searches; Esc clears."
                : "Find text in the message, exception, properties or logger columns, on the server. Enter searches; Esc clears.";

        BtnEdit.ToolTip = table
            ? $"Change this {Noun(source)}, its name or which {(source.Kind == LogSourceKinds.Mongo ? "fields" : "columns")} are read"
            : "Change this folder, its name or which files are read";
        BtnReload.ToolTip = table
            ? $"Read the {Noun(source)} again now, without waiting for the next sweep"
            : "Look at the folder again now, without waiting for the next sweep";

        var view = _host.Reader.View(source.Id);

        if (view is null)
        {
            Notice(null, "Reading\u2026", bad: false);
        }
        else if (view.Problem is { } problem)
        {
            Notice($"Couldn't read this {(table ? Noun(source) : "folder")}", problem, bad: true);
        }
        else if (table && view.Entries.Count == 0)
        {
            // Nothing was ever read, so there is no older list for the warning to sit above.
            if (view.Stale is { } stale) Notice($"Couldn't read this {Noun(source)}", stale, bad: true);
            else Notice(null, $"No rows in {source.Table} yet.", bad: false);
        }
        else if (!table && view.Files.Count == 0)
        {
            Notice(null, $"No log files in {source.Id} yet. Paste .txt or .log files there and they show up within a few seconds.", bad: false);
        }
        else
        {
            NoticeCard.Visibility = Visibility.Collapsed;
            Listing.Visibility = Visibility.Visible;

            if (table)
            {
                PaintTable(view);
            }
            else
            {
                StaleNote.Visibility = Visibility.Collapsed;
                if (!ReferenceEquals(view, _painted)) PaintEntries(view, anchor: true);
            }
        }
    }

    /// <summary>Show a sentence in place of the entries, and let go of the entries.</summary>
    private void Notice(string? title, string text, bool bad)
    {
        Forget();

        Listing.Visibility = Visibility.Collapsed;
        NoticeCard.Visibility = Visibility.Visible;
        NoticeCard.Background = Palette.Of(bad ? Tone.BadTint : Tone.Card);
        NoticeCard.BorderBrush = Palette.Of(bad ? Tone.BadEdge : Tone.Line);

        NoticeTitle.Text = title ?? string.Empty;
        NoticeTitle.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        NoticeText.Text = text;
    }

    /// <summary>Nothing is built for the list: drop the rows, and the entry that was open.</summary>
    private void Forget()
    {
        _painted = null;
        ResetPage();

        if (_rows.Count == 0 && _picked is null) return;

        _quiet = true;
        EntryList.ItemsSource = null;
        _quiet = false;

        _rows = [];
        _picked = null;
        ShowDetail(null);
    }

    // ---------- the entries ----------

    /// <summary>Build the rows for a view under the filters now chosen, and keep the reader's place in them.</summary>
    /// <param name="anchor">
    /// A refresh and not a change of filter: the list stays where it was, by entry,
    /// instead of jumping back to the top while someone is reading.
    /// </param>
    private void PaintEntries(LogFolderView view, bool anchor)
    {
        _painted = view;
        SyncFiles(view);

        var rows = Filter(view);
        Fill(rows, anchor);
        PaintStatus(view, rows.Count);
    }

    /// <summary>Put rows in the list, keeping the open entry and, for a refresh, the reader's place.</summary>
    private void Fill(List<LogRow> rows, bool anchor)
    {
        var top = anchor ? TopEntry() : null;
        var hadFocus = EntryList.IsKeyboardFocusWithin;

        var pick = _picked;
        var picked = pick is null ? null : rows.Find(r => SameEntry(r.Entry, pick));

        _quiet = true;
        EntryList.ItemsSource = rows;
        EntryList.SelectedItem = picked;
        _quiet = false;

        _rows = rows;

        // The same entry may be a fresh object when its file was read again. Only a
        // different text repaints the pane, which would throw away its scroll and selection.
        if (picked is null)
        {
            if (pick is not null)
            {
                _picked = null;
                ShowDetail(null);
            }
        }
        else if (!ReferenceEquals(picked.Entry, pick))
        {
            var same = pick is not null && picked.Entry.Text == pick.Text && picked.Entry.Detail == pick.Detail;
            _picked = picked.Entry;
            if (!same) ShowDetail(_picked);
        }

        var moved = false;

        if (top is not null && rows.FindIndex(r => SameEntry(r.Entry, top)) is >= 0 and var at)
        {
            // The extent is stale until the list has been measured against its new rows.
            EntryList.UpdateLayout();
            Scroller()?.ScrollToVerticalOffset(at);
            moved = true;
        }
        else if (!anchor && picked is not null)
        {
            EntryList.ScrollIntoView(picked);
        }
        else
        {
            // A new list keeps the old one's offset otherwise: another folder, or
            // another filter, would open part-way down.
            Scroller()?.ScrollToTop();
        }

        if (hadFocus)
        {
            if (!moved) EntryList.UpdateLayout();
            FocusList();
        }
    }

    /// <summary>The filters have changed; the view has not. A table's page is read again instead (<see cref="LoadPage"/>).</summary>
    private void Refilter()
    {
        if (_painted is { IsTable: false } view) PaintEntries(view, anchor: false);
    }

    private LogSeverity Floor() => LevelBox.SelectedIndex switch
    {
        1 => LogSeverity.Info,
        2 => LogSeverity.Warning,
        3 => LogSeverity.Error,
        _ => LogSeverity.Unknown,
    };

    private List<LogRow> Filter(LogFolderView view)
    {
        var level = LevelBox.SelectedIndex;
        var floor = Floor();

        var term = SearchBox.Text.Trim();
        var file = _file;

        var rows = new List<LogRow>();

        // Unknown is below every floor, so it shows only under "All levels".
        foreach (var entry in view.Entries)
        {
            if (level > 0 && entry.Level < floor) continue;
            if (file is not null && !string.Equals(entry.Source, file, StringComparison.Ordinal)) continue;
            if (term.Length > 0 && !Holds(entry, term)) continue;

            rows.Add(new LogRow(entry));
        }

        // The view is oldest first.
        if (_newestFirst) rows.Reverse();
        return rows;
    }

    // The first line too: the message is the line less its time and level, and cut at 1,000 characters.
    private static bool Holds(LogEntry entry, string term) =>
        entry.FirstLine.Contains(term, StringComparison.OrdinalIgnoreCase)
        || entry.Message.Contains(term, StringComparison.OrdinalIgnoreCase)
        || entry.Detail?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    // Not by line number: for a file read from its last 32 MB, every number moves when it grows.
    private static bool SameEntry(LogEntry a, LogEntry b) =>
        ReferenceEquals(a, b)
        || (a.Time == b.Time && string.Equals(a.Source, b.Source, StringComparison.Ordinal)
            && string.Equals(a.FirstLine, b.FirstLine, StringComparison.Ordinal));

    /// <summary>The file filter lists the folder's files. The list is touched only when they change, or an open dropdown would shut under the pointer.</summary>
    private void SyncFiles(LogFolderView view)
    {
        if (view.IsTable)
        {
            _file = null;
            return;
        }

        var names = view.Files.Where(f => !f.OverBudget).Select(f => f.Name).ToList();
        if (_file is not null && !names.Contains(_file)) _file = null;

        var items = new List<string>(names.Count + 1) { "All files" };
        items.AddRange(names);

        _quiet = true;

        if (!items.SequenceEqual(_fileItems))
        {
            _fileItems = items;
            FileBox.ItemsSource = items;
        }

        var at = _file is null ? 0 : items.IndexOf(_file);
        if (FileBox.SelectedIndex != at) FileBox.SelectedIndex = at;

        _quiet = false;
    }

    private void PaintStatus(LogFolderView view, int shown)
    {
        StatusLine.Text = $"Showing {shown.ToString("N0", CultureInfo.CurrentCulture)} of "
                          + $"{view.Entries.Count.ToString("N0", CultureInfo.CurrentCulture)} entries";

        NoMatch.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Text = view.Entries.Count == 0 ? "The log files in this folder hold no entries yet." : "Nothing matches these filters.";

        // Whatever the reading left out, said every time: a missing hour is never a silent one.
        var notes = new List<string>();
        var megabytes = LogFile.MaxBytes / (1024 * 1024);

        foreach (var file in view.Files)
        {
            if (file.Problem is { } problem) notes.Add($"{file.Name}: {problem}");
            if (file.SkippedBytes > 0) notes.Add($"Only the last {megabytes} MB of {file.Name} was read");

            if (file.Dropped > 0)
                notes.Add($"{file.Name}: {Count(file.Dropped, "older entry", "older entries")} not kept, to stay within the size limits");
        }

        var older = view.Files.Count(f => f.OverBudget);
        if (older > 0) notes.Add($"{Count(older, "older file")} not read \u2014 newer ones fill the view");

        if (view.Trimmed > 0)
            notes.Add($"{Count(view.Trimmed, "oldest entry", "oldest entries")} not shown \u2014 newer ones fill the view");

        NoteList.ItemsSource = notes;
    }

    // ---------- a table's page ----------

    private bool Filtered => _floor > LogSeverity.Unknown || _search is not null;

    private bool OnTable => Current() is { } source && IsTable(source);

    /// <summary>
    /// A table. The list is a page read from the server, not what the poll holds; a new reading
    /// from the poll reads the newest page again, but only when that page is on screen unfiltered —
    /// a level or a search reads back through the table, so it runs again only when asked.
    /// </summary>
    private void PaintTable(LogFolderView view)
    {
        // Edited to read differently under the same id: the rows on screen and the page cursors belong to the old reading.
        if (_painted is { } before && !before.Source.ReadsSameAs(view.Source)) ResetPage();

        var newest = view.Entries.Count > 0 ? view.Entries[^1] : null;
        var fresh = !ReferenceEquals(newest, _seenNewest);
        _painted = view;
        _seenNewest = newest;

        if (_page is null)
        {
            if (!_loading) LoadPage(PageMove.Open);
            return;
        }

        if (!fresh || _loading) return;

        if (_above.Count == 0 && !Filtered)
        {
            LoadPage(PageMove.Again);
        }
        else
        {
            _newer = true;
            PaintPageStatus();
        }
    }

    /// <summary>Read a page from the server and show it. One at a time: a new ask drops the answer to the one before.</summary>
    private async void LoadPage(PageMove move)
    {
        if (_stopped || _painted is not { IsTable: true } || Current() is not { } source || !IsTable(source)) return;

        object? before;
        List<object?> above;
        LogSeverity floor;
        string? search;

        switch (move)
        {
            case PageMove.Open:
                (before, above, floor, search) = (null, [], Floor(), LogTableRows.SearchTerm(SearchBox.Text));
                break;
            case PageMove.Newest:
                (before, above, floor, search) = (null, [], _floor, _search);
                break;
            case PageMove.Newer when _above.Count > 0:
                (before, above, floor, search) = (_above[^1], _above[..^1], _floor, _search);
                break;
            case PageMove.Older when _page?.Older is { } older:
                (before, above, floor, search) = (older, [.. _above, _before], _floor, _search);
                break;
            case PageMove.Again:
                (before, above, floor, search) = (_before, [.. _above], _floor, _search);
                break;
            default:
                return;
        }

        if (Same(_restFor, source.Id) && DateTime.UtcNow < _restUntil)
        {
            var wait = Math.Max(1, (int)Math.Ceiling((_restUntil - DateTime.UtcNow).TotalSeconds));
            var why = $"The server was slow to answer, so it isn't asked again for another {wait} seconds.";

            if (_page is not null)
            {
                StaleNote.Text = why;
                StaleNote.Visibility = Visibility.Visible;
                return;
            }

            // Nothing on screen yet: say it there, and let Reload ask for this page once the wait is over.
            (_before, _floor, _search) = (before, floor, search);
            _above.Clear();
            _above.AddRange(above);
            _page = TablePageView.Failed(why, slow: true);
            PaintPage(anchor: false);
            return;
        }

        _asked = (floor, search, _pageSize);
        var cts = new CancellationTokenSource(PageWait);
        _pageCts?.Cancel();
        _pageCts = cts;

        var ask = ++_pageAsk;
        _loading = true;

        // The first page of a table: nothing of what was on screen before stays while it is read.
        if (_page is null) PaintPage(anchor: false);
        else PaintPager();

        TablePageView page;
        try
        {
            var query = new TablePageQuery(_pageSize, before, floor, search);
            page = await Task.Run(() => _host.Reader.PageAsync(source.Id, query, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            page = TablePageView.Failed("The server didn't answer in time.", slow: true);
        }
        catch (Exception ex)
        {
            // Not expected: the reader says what went wrong in words. Type and stack only, as everywhere a log is read.
            CrashLog.Write($"LogsPage, reading a page: {ex.GetType().FullName}{Environment.NewLine}{ex.StackTrace}", null);
            page = TablePageView.Failed("The page couldn't be read.");
        }
        finally
        {
            if (ReferenceEquals(_pageCts, cts)) _pageCts = null;
            cts.Dispose();
        }

        // Something else was asked for meanwhile, another source picked, or the page closed.
        if (ask != _pageAsk || _stopped) return;
        _loading = false;
        _pageReadAt = DateTime.UtcNow;
        if (page.Slow) (_restUntil, _restFor) = (_pageReadAt + PageRest, source.Id);

        if (page.Problem is { } problem && move == PageMove.Again && _page is { Problem: null })
        {
            _againProblem = problem;
        }
        else
        {
            (_before, _floor, _search) = (before, floor, search);
            _above.Clear();
            _above.AddRange(above);
            _page = page;
            _againProblem = null;
            if (before is null && page.Problem is null) _newer = false;
        }

        PaintPage(anchor: move == PageMove.Again);
    }

    private void PaintPage(bool anchor)
    {
        if (_painted is not { IsTable: true }) return;

        Fill((_page?.Entries ?? []).Select(e => new LogRow(e)).ToList(), anchor);

        StaleNote.Text = _againProblem is { } why ? $"Couldn't read this page again: {why} It shows what was read before." : string.Empty;
        StaleNote.Visibility = _againProblem is null ? Visibility.Collapsed : Visibility.Visible;

        PaintPager();
        PaintPageStatus();
    }

    /// <summary>The buttons stay enabled while a page is read, so the keyboard isn't thrown off them; a press then does nothing.</summary>
    private void PaintPager()
    {
        var number = _above.Count + 1;
        PageLabel.Text = _loading ? "Reading\u2026" : $"Page {number.ToString("N0", CultureInfo.CurrentCulture)}";

        var newer = number > 1;
        var older = _page?.Older is not null;
        if (newer) BtnNewest.IsEnabled = BtnNewer.IsEnabled = true;
        if (older) BtnOlder.IsEnabled = true;

        // A button that turns itself off would drop the keyboard to the window: hand it on first.
        if (!older && BtnOlder.IsKeyboardFocused) (newer ? BtnNewer : (UIElement)EntryList).Focus();
        if (!newer && (BtnNewer.IsKeyboardFocused || BtnNewest.IsKeyboardFocused)) (older ? BtnOlder : (UIElement)EntryList).Focus();

        if (!newer) BtnNewest.IsEnabled = BtnNewer.IsEnabled = false;
        if (!older) BtnOlder.IsEnabled = false;
    }

    private void PaintPageStatus()
    {
        var count = _page?.Entries.Count ?? 0;

        var line = _page is null ? "Reading\u2026" : $"{Count(count, "row")} on this page, newest first";
        if (_search is { } term) line += $", holding \"{term}\"";

        var typed = LogTableRows.SearchTerm(SearchBox.Text);
        if (_page is not null && typed != _search)
            line += typed is null ? " \u00b7 Enter clears the search" : $" \u00b7 Enter searches for \"{typed}\"";

        StatusLine.Text = line;

        NoMatch.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Text = _page switch
        {
            null => "Reading\u2026",
            { Problem: { } problem } => $"Couldn't read this page. {problem}",
            _ when _above.Count > 0 => "No older rows.",
            _ when Filtered => "Nothing in this table matches these filters.",
            _ => "No rows in this table yet.",
        };

        var notes = new List<string>();
        if (_page is { TiesCut: true, Entries: [.., { Time: { } at }] })
            notes.Add($"More rows share the time {at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} "
                      + $"than one page takes; only {LogTableRows.MaxTies.ToString("N0", CultureInfo.CurrentCulture)} of them are shown.");
        if (_newer)
            notes.Add(_above.Count > 0 ? "Newer rows have arrived since. Newest shows them." : "New rows have arrived since this page was read. Reload reads them.");

        NoteList.ItemsSource = notes;
    }

    /// <summary>Let go of the page and drop any answer still on its way.</summary>
    private void ResetPage()
    {
        _pageTimer.Stop();
        _pageCts?.Cancel();
        _pageCts = null;
        _pageAsk++;
        _loading = false;
        _page = null;
        _before = null;
        _above.Clear();
        _newer = false;
        _againProblem = null;
    }

    private void OnPageNewest(object sender, RoutedEventArgs e)
    {
        if (!_loading) LoadPage(PageMove.Newest);
    }

    private void OnPageNewer(object sender, RoutedEventArgs e)
    {
        if (!_loading) LoadPage(PageMove.Newer);
    }

    private void OnPageOlder(object sender, RoutedEventArgs e)
    {
        if (!_loading) LoadPage(PageMove.Older);
    }

    private void OnPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_quiet) return;

        _pageSize = PageSizes[Math.Clamp(PageSizeBox.SelectedIndex, 0, PageSizes.Length - 1)];
        if (!OnTable) return;

        _pageTimer.Stop();
        _pageTimer.Start();
    }

    // ---------- scrolling and focus ----------

    private ScrollViewer? Scroller()
    {
        EntryList.ApplyTemplate();
        return EntryList.Template.FindName("Scroller", EntryList) as ScrollViewer;
    }

    /// <summary>The entry at the top of the list, or null when it is at the top: what a refresh puts back.</summary>
    private LogEntry? TopEntry()
    {
        // Scrolling is by item, so the offset is the index of the first row.
        var at = Scroller() is { } scroller ? (int)scroller.VerticalOffset : 0;
        return at > 0 && at < _rows.Count ? _rows[at].Entry : null;
    }

    /// <summary>Rebuilding the rows takes the keyboard with the row that had it.</summary>
    private void FocusList()
    {
        if (EntryList.SelectedItem is { } item
            && EntryList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            container.Focus();
        else
            EntryList.Focus();
    }

    // ---------- the entry ----------

    private void OnEntryPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_quiet) return;

        _picked = (EntryList.SelectedItem as LogRow)?.Entry;
        ShowDetail(_picked);
    }

    private void ShowDetail(LogEntry? entry)
    {
        CopyNote.Visibility = Visibility.Collapsed;

        if (entry is null)
        {
            DetailBody.Visibility = Visibility.Collapsed;
            DetailEmpty.Visibility = Visibility.Visible;
            DetailText.Clear();
            return;
        }

        DetailBody.Visibility = Visibility.Visible;
        DetailEmpty.Visibility = Visibility.Collapsed;

        var look = LogRow.Look(entry.Level);
        DetailPill.Background = look.Back;
        DetailLevel.Foreground = look.Ink;
        DetailLevel.Text = look.Word.ToUpperInvariant();

        DetailTime.Text = entry.Time is { } time
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)
            : "No time in this entry";

        // A table row has no line: its number, when it has one, is the id column's.
        DetailSource.Text = _painted is { IsTable: true }
            ? entry.Line > 0 ? $"{entry.Source} #{entry.Line.ToString(CultureInfo.InvariantCulture)}" : entry.Source
            : $"{entry.Source}:{entry.Line.ToString(CultureInfo.InvariantCulture)}";
        CutNote.Visibility = entry.Cut ? Visibility.Visible : Visibility.Collapsed;

        // A JSON entry is one line with its stack trace escaped inside it; what was
        // read out of it is the readable form. Copy still takes the line as written.
        DetailText.Text = entry.Structured && entry.Detail is { } detail
            ? entry.Message + "\n\n" + detail
            : entry.Text;
        DetailText.ScrollToHome();
    }

    private void OnCopy(object sender, RoutedEventArgs e) => CopyPicked();

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control || _picked is null) return;

        CopyPicked();
        e.Handled = true;
    }

    private void CopyPicked()
    {
        if (_picked is not { } entry) return;

        try
        {
            // Production text: kept out of Win+V history and the cloud clipboard, which
            // would otherwise hold it past the paste, or send it off the machine.
            var data = new System.Windows.DataObject();
            data.SetText(entry.Text.ReplaceLineEndings("\r\n"));
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream([0, 0, 0, 0]));
            data.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream([0, 0, 0, 0]));
            data.SetData("CanUploadToCloudClipboard", new System.IO.MemoryStream([0, 0, 0, 0]));
            System.Windows.Clipboard.SetDataObject(data, copy: true);
            Say("Copied.", Tone.Faint);
        }
        catch (COMException)
        {
            // Another program has the clipboard open; WPF has already retried.
            Say("Couldn't copy \u2014 the clipboard is busy.", Tone.BadInk);
        }
    }

    private void Say(string text, string tone)
    {
        CopyNote.Text = text;
        CopyNote.Foreground = Palette.Of(tone);
        CopyNote.Visibility = Visibility.Visible;
    }

    // ---------- the filters ----------

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_quiet) return;

        if (OnTable)
        {
            _pageTimer.Stop();
            _pageTimer.Start();
        }
        else
        {
            Refilter();
        }
    }

    private void OnFileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_quiet) return;

        _file = FileBox.SelectedIndex > 0 ? FileBox.SelectedItem as string : null;
        Refilter();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_quiet) return;

        // A table is searched on the server, so only on Enter — but emptying the box drops the search at once.
        if (OnTable)
        {
            if (LogTableRows.SearchTerm(SearchBox.Text) is null && _search is not null) LoadPage(PageMove.Open);
            else if (_painted is { IsTable: true }) PaintPageStatus();
            return;
        }

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (e.IsRepeat) return;

        if (OnTable)
        {
            // The same search already on its way: a second Enter would only start it over.
            if (_loading && _asked == (Floor(), LogTableRows.SearchTerm(SearchBox.Text), _pageSize)) return;

            _pageTimer.Stop();
            LoadPage(PageMove.Open);
        }
        else
        {
            _searchTimer.Stop();
            Refilter();
        }
    }

    private void OnSortFlip(object sender, RoutedEventArgs e)
    {
        _newestFirst = !_newestFirst;
        SortButton.Content = _newestFirst ? "Newest first" : "Oldest first";
        SortButton.ToolTip = _newestFirst
            ? "Showing the newest entries first. Press to show the oldest first."
            : "Showing the oldest entries first. Press to show the newest first.";

        Refilter();
    }

    // ---------- the source's buttons ----------

    private async void OnAddSource(object sender, RoutedEventArgs e) => await Edit(null);

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Current() is { } source) await Edit(source);
    }

    private async Task Edit(LogSourceEntry? source)
    {
        var form = new LogSourceForm(_host, source);

        // False when the drawer was closed on it, or the window went while it was open.
        if (!await _drawer.OpenAsync(form) || _stopped || !IsLoaded) return;

        // The folder it saved, so a new one is the one you land on. A removed one
        // names none, and the page falls back to the first.
        if (form.Chosen is { } chosen) _sourceId = chosen;

        Repaint();
    }

    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_opening || Current() is not { } source || IsTable(source) || source.Path is not { } path) return;

        _opening = true;
        string? failure;

        try
        {
            // The disk is asked, and a mapped drive that has gone can take a while to say so.
            failure = await Task.Run(() => DesktopActions.OpenFolder(path));
        }
        finally
        {
            _opening = false;
        }

        // The answer is about that folder; another may be on screen by now.
        if (_stopped || !Same(Current()?.Id, source.Id)) return;

        HeadNote.Text = failure ?? string.Empty;
        HeadNote.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        HeadNote.Visibility = Visibility.Collapsed;

        // Only the table on screen is polled early, and not more than every 15 seconds; its page is read again, not more than every 5.
        var table = Current() is { } source && IsTable(source) ? source.Id : null;
        _host.Reader.ReadNow(table);
        if (table is not null && !_loading && DateTime.UtcNow - _pageReadAt >= ReloadGap) LoadPage(PageMove.Again);
    }

    // ---------- keys ----------

    /// <summary>
    /// Ctrl+F to the search, Esc to clear it. The main window passes every key
    /// here, ahead of the controls, while this page is showing.
    /// </summary>
    internal void OnShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (!SearchBox.IsVisible) return;

            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    // ---------- layout ----------

    /// <summary>The folder pane narrows with the page, at the same width and to the same sizes as the Projects one.</summary>
    private void OnPageSized(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= MainWindow.NotesListsWide;
        if (wide == _wide) return;

        _wide = wide;
        SourceColumn.Width = new GridLength(wide ? 240 : 180);
    }

    private void OnRightSized(object sender, SizeChangedEventArgs e)
    {
        var pagerBeside = e.NewSize.Width >= PagerBesideFrom;
        if (pagerBeside != _pagerBeside)
        {
            _pagerBeside = pagerBeside;
            Grid.SetRow(PagerBar, pagerBeside ? 0 : 1);
            Grid.SetColumn(PagerBar, pagerBeside ? 1 : 0);
            PagerBar.Margin = pagerBeside ? new Thickness(12, 0, 0, 0) : new Thickness(0, 6, 0, 0);
        }

        var beside = e.NewSize.Width >= SearchBesideFrom;
        if (beside == _searchBeside) return;

        _searchBeside = beside;
        Grid.SetRow(SearchBox, beside ? 0 : 1);
        Grid.SetColumn(SearchBox, beside ? 2 : 0);
        Grid.SetColumnSpan(SearchBox, beside ? 1 : 4);
        SearchBox.Margin = beside ? new Thickness(8, 0, 0, 0) : new Thickness(0, 8, 0, 0);
    }
}
