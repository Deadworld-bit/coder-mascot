using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CoderMascot.Core;

// UseWindowsForms pulls in System.Drawing, which has its own Brush and Color.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace CoderMascot.UI;

/// <summary>What the dashboard needs from the app, so it never reaches into it.</summary>
public interface IDashboardHost
{
    CoderConfig Config { get; }
    WorkspaceSnapshot? Workspace { get; }
    SessionSnapshot? Sessions { get; }
    LoadSnapshot Load { get; }
    IReadOnlyList<DevServer> Leftovers { get; }
    TimeSpan? WorkspaceIdle { get; }
    StateHistory History { get; }
    NoteBook Notes { get; }

    /// <summary>Stick a note to the desktop, or take it back into the list.</summary>
    void StickToDesktop(string noteId, bool stick);

    /// <summary>A note was edited here; repaint it if it is also on the desktop.</summary>
    void NoteChanged(string noteId);

    /// <summary>The global shortcut actually in force, or null if none is.</summary>
    string? NoteHotkey { get; }

    /// <summary>
    /// Everything listening on this machine. Runs off the UI thread and is
    /// cached briefly, so a dashboard repainting once a second doesn't sweep the
    /// TCP table once a second.
    /// </summary>
    Task<IReadOnlyList<Listener>> PortsAsync(bool fresh = false);

    void CheckNow();
    void OpenCoder();
    Task<string?> TransitionAsync(string transition);
    void RescanLeftovers();

    /// <summary>Open the connect-to-Coder window.</summary>
    void ShowSetup();
    void SetAlertPolicy(AlertPolicy policy);

    /// <summary>Turn logon startup on or off. Returns null, or why it failed.</summary>
    string? SetStartWithWindows(bool on);

    void SettingsChanged();
}

// Rows are records rather than live objects: the lists are rebuilt each tick,
// which is cheaper to reason about than change notification for twenty items
// that refresh on a timer anyway.
public sealed record SessionRow(string Folder, string Activity, string Age, Brush Pip, string? Match);
public sealed record CulpritRow(string Title, string Sub, string Name);
public sealed record LeftoverRow(string Title, string Sub, string Action, int Pid, bool IsWorkspace);
public sealed record HistoryRow(string Time, string Title, string Detail, Brush Pip);
public sealed record NoteRow(string Id, string Text, bool Done, bool Pinned,
                             string When, string PinLabel, Brush Ink, double Fade,
                             string Group, IReadOnlyList<string> Groups, Visibility PickerShown,
                             Brush Paper, bool Stuck, string StickLabel);
public sealed record GroupTab(string? Name, string Label, Brush Back, Brush Edge);
public sealed record PortRow(string Port, string Title, string Sub, Brush Pip, int Number, int Pid);

/// <summary>One tab, and how much is behind it.</summary>
public sealed record TabRow(string Key, string Title, string Tip, string Badge,
                            Visibility BadgeShown, FontWeight Weight,
                            Brush Ink, Brush Mark, Brush BadgeBack, Brush BadgeInk);

/// <summary>
/// The detail view: everything the badge cannot say in one colour.
///
/// Deliberately pull-based on a timer rather than pushed at from the monitors.
/// Most of what it shows is elapsed time — "thinking 4 min", "up 6h" — which
/// changes every second without any underlying event, so a push model would
/// either need a fake event every second or show stale numbers. Nothing here
/// polls anything remote; it re-reads the snapshots the app already holds.
/// </summary>
public partial class DashboardWindow : Window
{
    private readonly IDashboardHost _host;
    private readonly DispatcherTimer _tick;

    /// <summary>Last port sweep, and whether one is already in flight.</summary>
    private IReadOnlyList<Listener> _ports = [];
    private bool _scanning;

    /// <summary>Culprits cost a full process walk, so they refresh far less often.</summary>
    private DateTime _culpritsAt = DateTime.MinValue;
    private List<LoadCulprit> _culprits = [];

    private bool _loadingSettings;

    /// <summary>Which tab is showing. Status, because that is what the app is for.</summary>
    private string _tab = "Status";

    /// <summary>How many rows the last paint put on each tab, for the badges.</summary>
    private int _leftoverCount;
    private int _noteCount;
    private int _portCount;

    /// <summary>An in-place note edit that hasn't been written to disk yet.</summary>
    private bool _noteEdits;

    /// <summary>
    /// The list the name box is renaming, or null when it is naming a new one.
    /// Only meaningful while <c>GroupEditor</c> is visible.
    /// </summary>
    private string? _renaming;

    public DashboardWindow(IDashboardHost host)
    {
        _host = host;
        InitializeComponent();

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _tick.Tick += (_, _) => Refresh();

        // Only run while the window is actually on screen. A background timer
        // rebuilding five lists behind a closed window is exactly the kind of
        // waste this app complains about.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { LoadSettings(); PaintNotes(); Refresh(); RefreshPorts(fresh: true); ShowTab(_tab); _tick.Start(); }
            else { _tick.Stop(); CommitAndPrune(); }
        };

        // Notes are the one thing here that isn't re-derivable from a poll, so
        // they get written on the way out as well as after every action.
        Closed += (_, _) => CommitAndPrune();

    }

    // Closing really closes, and the app recreates it next time. The tempting
    // alternative — cancel the close and hide instead, to keep scroll position —
    // means a window that refuses to shut, and Application.Shutdown() honours
    // that refusal. Losing a scroll position is a better trade than a process
    // that won't quit.

    /// <summary>Show it, or bring it back to the front if it's already up.</summary>
    public void Summon(bool focusNotes = false)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        if (!focusNotes) return;

        // Asked for a note, so put the notes on screen: the box is on another
        // tab now, and focusing something nobody can see is worse than useless.
        ShowTab("Notes");

        // After layout, or there is nothing to scroll to yet on the first open
        // and the caret lands in a box that is still zero-sized.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NotesCard.BringIntoView();
            NoteEntry.Focus();
        });
    }

    // ---------- painting ----------

    private void Refresh()
    {
        var ws = _host.Workspace;
        var sessions = _host.Sessions;
        var load = _host.Load;

        PaintHeader(ws);
        PaintSessions(sessions);
        PaintWorkspace(ws);
        PaintMachine(load);
        PaintLeftovers();
        PaintHistory();
        RefreshPorts();
        PaintTabs();
    }

    // ---------- tabs ----------

    /// <summary>
    /// The strip, with a count on each tab.
    ///
    /// The counts are the point. Behind a tab, a thing that wants attention is
    /// invisible — which is how a dev server left running for two days goes
    /// unnoticed — so the number comes out to where it can be seen without
    /// opening anything.
    /// </summary>
    private void PaintTabs()
    {
        TabStrip.ItemsSource = new List<TabRow>
        {
            Tab("Status", "Workspace, sessions, this machine, and what's been left running",
                _leftoverCount, urgent: _leftoverCount > 0),
            Tab("Notes", "Your notes, and the lists they are kept in", _noteCount, urgent: false),
            Tab("Ports", "What is listening on this machine", _portCount, urgent: false),
            Tab("Settings", "How loud the mascot is, and what it watches", 0, urgent: false),
        };
    }

    private TabRow Tab(string key, string tip, int count, bool urgent)
    {
        var on = _tab == key;

        return new TabRow(
            Key: key,
            Title: key,
            Tip: tip,
            Badge: count.ToString(CultureInfo.InvariantCulture),
            BadgeShown: count > 0 ? Visibility.Visible : Visibility.Collapsed,
            Weight: on ? FontWeights.SemiBold : FontWeights.Normal,
            Ink: Freeze(on ? Tone.Ink : Tone.Faint),

            // Transparent rather than absent, so the tab does not change height
            // when it becomes the selected one and shove the whole page down.
            Mark: Freeze(on ? Tone.Pick : Tone.Rail),
            BadgeBack: Freeze(urgent ? Tone.BadEdge : Tone.Lift),
            BadgeInk: Freeze(urgent ? Tone.BadInk : Tone.Ink));
    }

    private void OnPickTab(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TabRow tab) return;

        ShowTab(tab.Key);
    }

    private void ShowTab(string key)
    {
        _tab = key;

        TabStatus.Visibility = key == "Status" ? Visibility.Visible : Visibility.Collapsed;
        TabNotes.Visibility = key == "Notes" ? Visibility.Visible : Visibility.Collapsed;
        TabPorts.Visibility = key == "Ports" ? Visibility.Visible : Visibility.Collapsed;
        TabSettings.Visibility = key == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        PaintTabs();
    }

    private void PaintHeader(WorkspaceSnapshot? ws)
    {
        var state = ws?.State ?? MascotState.Unknown;
        StatePip.Fill = Pip(state);
        HeadTitle.Text = ws?.WorkspaceName is { Length: > 0 } n
            ? $"{state.Title()} — {n}"
            : state.Title();
        HeadDetail.Text = ws?.Detail ?? string.Empty;
    }

    private void PaintSessions(SessionSnapshot? s)
    {
        if (s is null || !s.Ok)
        {
            SessionList.ItemsSource = null;
            SessionsEmpty.Visibility = Visibility.Visible;
            SessionsEmpty.Text = s?.Detail is { Length: > 0 } d
                ? $"Can't read sessions — {d}"
                : "Not checked yet.";
            return;
        }

        if (!s.Installed)
        {
            SessionList.ItemsSource = null;
            SessionsEmpty.Visibility = Visibility.Visible;
            SessionsEmpty.Text =
                "The workspace-side hooks aren't installed, so nothing here can be seen. "
                + "In a workspace terminal:\n"
                + "    bash ~/workspace/projects/coder-mascot/workspace/install-hooks.sh";
            return;
        }

        var rows = s.Sessions.Select(line => new SessionRow(
            Folder: string.IsNullOrWhiteSpace(line.Folder) ? "(unknown folder)" : line.Folder!,
            Activity: line.Activity,
            Age: line.IdleFor > 0 ? Friendly.Duration(line.IdleFor) : string.Empty,
            Pip: SessionPip(line.Status),
            Match: line.Folder)).ToList();

        SessionList.ItemsSource = rows;
        SessionsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count == 0) SessionsEmpty.Text = "No Claude sessions running.";
    }

    private void PaintWorkspace(WorkspaceSnapshot? ws)
    {
        var facts = new List<string>();

        if (ws?.BuildStatus is { Length: > 0 } b) facts.Add($"Build: {b}");
        if (ws?.AgentStatus is { Length: > 0 } a) facts.Add($"Agent: {a}");
        if (ws?.LifecycleState is { Length: > 0 } l) facts.Add($"Lifecycle: {l.Replace('_', ' ')}");

        if (ws?.LastUsedAt is { } used)
            facts.Add($"Last used: {Friendly.Duration((DateTimeOffset.UtcNow - used).TotalSeconds)} ago");

        if (ws?.TimeToDeadline is { } left && left > TimeSpan.Zero)
            facts.Add($"Auto-stop in {Friendly.Duration(left.TotalSeconds)}");

        WorkspaceFacts.Text = facts.Count > 0 ? string.Join("   ·   ", facts) : "—";

        // Only offer what makes sense: starting a running workspace is a no-op
        // that returns an error, which reads as the app being broken.
        var running = ws?.BuildStatus is "running";
        var known = ws?.WorkspaceId is { Length: > 0 };
        BtnStart.IsEnabled = known && !running;
        BtnStop.IsEnabled = known && running;
        BtnRestart.IsEnabled = known && running;
        BtnLogin.IsEnabled = !string.IsNullOrWhiteSpace(_host.Config.Url);
    }

    private void PaintMachine(LoadSnapshot load)
    {
        CpuText.Text = $"{load.CpuPercent:0}%";
        MemText.Text = $"{load.MemoryPercent:0}%";

        // The bars are drawn against the *warning* line, not against 100 — the
        // question being asked is "how close am I to the thing that nags me",
        // and a bar that never leaves the first third answers nothing.
        SetBar(CpuBar, load.CpuPercent, _host.Config.CpuWarnPercent, Tone.Cpu);
        SetBar(MemBar, load.MemoryPercent, _host.Config.MemoryWarnPercent, Tone.Mem);

        // A full process walk, so nothing like every tick. Ten seconds is well
        // inside how fast this list actually changes.
        if ((DateTime.UtcNow - _culpritsAt).TotalSeconds >= 10)
        {
            _culpritsAt = DateTime.UtcNow;
            _culprits = SystemMonitor.Culprits();
        }

        CulpritList.ItemsSource = _culprits.Take(4).Select(c => new CulpritRow(
            Title: c.Count > 1 ? $"{c.Name} ×{c.Count}" : c.Name,
            Sub: $"{c.MemoryGb:0.0} GB",
            Name: c.Name)).ToList();
    }

    /// <summary>
    /// Draw one gauge. Note the base colour is passed in rather than read back
    /// off the bar: reading it means that once the bar turns warning-orange it
    /// has forgotten what it used to be, and it never turns back.
    /// </summary>
    private static void SetBar(System.Windows.Controls.Border bar, double value, double limit, string calm)
    {
        if (bar.Parent is not FrameworkElement track) return;

        var span = Math.Max(1, track.ActualWidth);
        var fraction = Math.Clamp(value / Math.Max(1, limit), 0, 1);

        bar.Width = span * fraction;
        bar.Background = Freeze(value >= limit ? Tone.Stalled : calm);
    }

    private void PaintLeftovers()
    {
        var rows = new List<LeftoverRow>();

        if (_host.WorkspaceIdle is { } idle && _host.Workspace?.WorkspaceId is { Length: > 0 })
        {
            rows.Add(new LeftoverRow(
                Title: $"Workspace {_host.Workspace?.WorkspaceName}",
                Sub: $"Nobody has touched it for {Friendly.Duration(idle.TotalSeconds)}",
                Action: "Stop",
                Pid: 0,
                IsWorkspace: true));
        }

        foreach (var s in _host.Leftovers)
        {
            rows.Add(new LeftoverRow(
                Title: s.Label,
                Sub: $"Listening for {Friendly.Duration(s.Uptime.TotalSeconds)} · pid {s.Pid}",
                Action: "Close",
                Pid: s.Pid,
                IsWorkspace: false));
        }

        _leftoverCount = rows.Count;
        LeftoverList.ItemsSource = rows;
        LeftoversEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PaintHistory()
    {
        HistoryList.ItemsSource = _host.History.Recent.Take(12).Select(h => new HistoryRow(
            Time: h.At.ToString("HH:mm", CultureInfo.InvariantCulture),
            Title: h.State.Title(),
            Detail: h.Detail,
            Pip: Pip(h.State))).ToList();
    }

    // ---------- colours ----------

    private static Brush Pip(MascotState state) => Freeze(Tone.For(state));

    private static Brush SessionPip(string status) => Freeze(status switch
    {
        "waiting" => Tone.Asking,
        "stalled" => Tone.Stalled,
        "running" => Tone.Starting,
        _ => Tone.Live,
    });

    private static readonly Dictionary<string, Brush> Brushes = [];

    /// <summary>
    /// Frozen and cached. These are rebuilt for every row on every tick, and an
    /// unfrozen brush per row is a lot of garbage for a colour that never
    /// changes.
    /// </summary>
    private static Brush Freeze(string hex)
    {
        if (Brushes.TryGetValue(hex, out var cached)) return cached;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        Brushes[hex] = brush;
        return brush;
    }

    // ---------- actions ----------

    private void OnCheckNow(object sender, RoutedEventArgs e)
    {
        _host.CheckNow();
        _host.RescanLeftovers();
        _culpritsAt = DateTime.MinValue;
        RefreshPorts(fresh: true);
    }

    private void OnOpenCoder(object sender, RoutedEventArgs e) => _host.OpenCoder();

    private void OnGoToSession(object sender, RoutedEventArgs e)
    {
        if (Row<SessionRow>(sender) is not { } row) return;

        if (!DesktopActions.FocusWindowFor(row.Match))
        {
            Say($"Couldn't find a window for \"{row.Folder}\". "
                + "It may be running inside the workspace rather than on this machine.");
        }
    }

    private async void OnStartWorkspace(object sender, RoutedEventArgs e) => await Transition("start", "Start");

    private async void OnStopWorkspace(object sender, RoutedEventArgs e) => await Transition("stop", "Stop");

    private async void OnRestartWorkspace(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Restart the workspace?\n\nEvery Claude session inside it will be killed.")) return;

        var error = await _host.TransitionAsync("stop");
        if (error is not null) { Say(error); return; }

        // Deliberately not chained to the stop completing. Coder queues the
        // start behind the stop build itself, and waiting here would block the
        // UI thread on a build that takes minutes.
        error = await _host.TransitionAsync("start");
        if (error is not null) Say(error);
    }

    private async Task Transition(string transition, string label)
    {
        if (transition == "stop" &&
            !Confirm("Stop the workspace?\n\nEvery Claude session inside it will be killed."))
            return;

        var error = await _host.TransitionAsync(transition);
        if (error is not null) Say($"{label} failed — {error}");
    }

    private void OnShowSetup(object sender, RoutedEventArgs e) => _host.ShowSetup();

    private void OnCoderLogin(object sender, RoutedEventArgs e)
    {
        var url = _host.Config.Url;
        if (string.IsNullOrWhiteSpace(url)) return;

        if (CoderCli.Resolve(_host.Config) is not { } cli)
        {
            Say("Couldn't find the coder CLI on this machine. Install it, or set "
                + "\"coderCli\" in the config file to its full path.");
            return;
        }

        if (DesktopActions.CoderLogin(cli, url!) is { } error)
            Say($"Couldn't open a terminal — {error}");
    }

    private void OnCloseFamily(object sender, RoutedEventArgs e)
    {
        if (Row<CulpritRow>(sender) is not { } row) return;

        if (!Confirm($"Ask every {row.Name} window to close?\n\n"
                     + "This is the same as clicking the X on each — anything unsaved will still prompt."))
            return;

        var (asked, problem) = ProcessControl.CloseFamily(row.Name);
        if (problem is not null) Say(problem);
        else if (asked == 0) Say($"Nothing to close — no {row.Name} window responded.");

        _culpritsAt = DateTime.MinValue;
    }

    private async void OnLeftoverAction(object sender, RoutedEventArgs e)
    {
        if (Row<LeftoverRow>(sender) is not { } row) return;

        if (row.IsWorkspace)
        {
            await Transition("stop", "Stop");
            return;
        }

        if (ProcessControl.Close(row.Pid) is { } problem) Say(problem);
        _host.RescanLeftovers();
    }

    // ---------- notes ----------

    /// <summary>
    /// Rebuild the note rows.
    ///
    /// Deliberately NOT called from the one-second tick that drives everything
    /// else on this window: these rows contain live text boxes, and replacing
    /// them under the caret would eat every second keystroke. Notes are redrawn
    /// on deliberate actions only — add, pin, done, delete, filter.
    /// </summary>
    private void PaintNotes()
    {
        var notes = _host.Notes;

        // Here rather than on blur: a note emptied and then deleted while its
        // own TextBox is still on screen leaves the caret in a box wired to
        // nothing, and everything typed into it afterwards goes nowhere. This
        // is the moment the row itself is being replaced, so it is the only
        // moment the note behind it can safely go.
        if (notes.Prune() > 0) SaveNotes();

        var filter = NoteFilter.Text;
        var group = notes.ActiveGroup;
        var groups = notes.Groups;

        PaintGroupTabs(notes, group);

        // The picker is only worth the width once there is somewhere else for a
        // note to go.
        var picker = groups.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        var rows = notes.Search(filter, group).Select(n => new NoteRow(
            Id: n.Id,
            Text: n.Text,
            Done: n.Done,
            Pinned: n.Pinned,
            When: Stamp(n),
            PinLabel: n.Pinned ? "Unpin" : "Pin",
            Ink: Freeze(n.Done ? Tone.Faint : Tone.Ink),
            Fade: n.Done ? 0.65 : 1.0,
            Group: n.Group,
            Groups: groups,
            PickerShown: picker,
            Paper: Freeze(NoteColour.Of(n.Colour).Header),
            Stuck: n.Stuck,
            StickLabel: n.Stuck ? "On desktop" : "Stick")).ToList();

        NoteList.ItemsSource = rows;

        var here = notes.Count(group);
        NotesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotesEmpty.Text = here == 0
            ? group is null
                ? "Nothing noted yet. Anything you'd put on a sticky note round the monitor goes here."
                : $"Nothing in {group} yet."
            : $"No note here matches \u201c{filter.Trim()}\u201d.";

        _noteCount = notes.OpenCount(group);

        NotesCount.Text = here == 0
            ? string.Empty
            : $"{notes.OpenCount(group)} open · {notes.DoneCount(group)} done"
              + (rows.Count != here ? $" · showing {rows.Count}" : string.Empty);

        HotkeyHint.Text = _host.NoteHotkey is { } key
            ? $"{key} drops a note on the desktop from anywhere"
            : "No global shortcut is bound — set \"noteHotkey\" in the config file.";

        NoteHint.Text = groups.Count > 1
            ? $"Enter adds it to {notes.AddTarget} · Shift+Enter for another line"
            : "Enter adds it · Shift+Enter for another line";

        BtnClearDone.IsEnabled = notes.DoneCount(group) > 0;

        // Neither makes sense against "All": there is no one list to act on.
        BtnRenameGroup.IsEnabled = group is not null;
        BtnDeleteGroup.IsEnabled = group is not null && groups.Count > 1;
    }

    /// <summary>One tab per list, with what's still open in it, and All in front.</summary>
    private void PaintGroupTabs(NoteBook notes, string? active)
    {
        var tabs = new List<GroupTab> { Tab(null, "All", active is null, notes.OpenCount()) };

        foreach (var name in notes.Groups)
            tabs.Add(Tab(name, name, string.Equals(name, active, StringComparison.Ordinal),
                         notes.OpenCount(name)));

        GroupTabs.ItemsSource = tabs;
    }

    private static GroupTab Tab(string? name, string label, bool selected, int open) => new(
        Name: name,
        Label: open > 0 ? $"{label} ({open})" : label,
        Back: Freeze(selected ? Tone.Pick : Tone.Lift),
        Edge: Freeze(selected ? Tone.Pick : Tone.Edge));

    /// <summary>
    /// When a note was last touched, as a clock time rather than "5 min ago" —
    /// the list is not repainted on the timer, so anything relative would sit
    /// there quietly going stale while the window is open.
    /// </summary>
    private static string Stamp(Note note)
    {
        var at = note.Updated.ToLocalTime();
        var when = at.Date == DateTimeOffset.Now.Date
            ? at.ToString("HH:mm", CultureInfo.InvariantCulture)
            : at.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);

        return note.Pinned ? $"Pinned · {when}" : when;
    }

    private void OnPickGroup(object sender, RoutedEventArgs e)
    {
        if (Row<GroupTab>(sender) is not { } tab) return;

        CommitNotes();
        HideGroupEditor();

        _host.Notes.ActiveGroup = tab.Name;
        SaveNotes();
        PaintNotes();
    }

    private void OnNewGroup(object sender, RoutedEventArgs e) => ShowGroupEditor(rename: null);

    private void OnRenameGroup(object sender, RoutedEventArgs e)
    {
        if (_host.Notes.ActiveGroup is { } current) ShowGroupEditor(rename: current);
    }

    /// <summary>The name box, either empty for a new list or filled for a rename.</summary>
    private void ShowGroupEditor(string? rename)
    {
        _renaming = rename;
        GroupName.Text = rename ?? string.Empty;
        GroupEditor.Visibility = Visibility.Visible;
        GroupName.Focus();
        GroupName.SelectAll();
    }

    private void HideGroupEditor()
    {
        GroupEditor.Visibility = Visibility.Collapsed;
        GroupName.Clear();
        _renaming = null;
    }

    private void OnCancelGroup(object sender, RoutedEventArgs e) => HideGroupEditor();

    private void OnGroupNameKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; HideGroupEditor(); return; }
        if (e.Key != System.Windows.Input.Key.Return) return;

        e.Handled = true;
        OnSaveGroup(sender, e);
    }

    private void OnSaveGroup(object sender, RoutedEventArgs e)
    {
        var name = GroupName.Text.Trim();
        if (name.Length == 0) { HideGroupEditor(); return; }

        var notes = _host.Notes;

        if (_renaming is { } current)
        {
            if (!notes.RenameGroup(current, name))
            {
                // The only way this fails on a non-empty name is a clash, and
                // merging two lists is not something to do by accident.
                Say($"There is already a list called \u201c{name}\u201d.");
                return;
            }
        }
        else
        {
            // A name that already exists is not an error here — it reads as
            // "take me to that list", which is what someone typing it wants.
            notes.AddGroup(name);
            notes.ActiveGroup = name;
        }

        HideGroupEditor();
        SaveNotes();
        PaintNotes();
        NoteEntry.Focus();
    }

    private void OnDeleteGroup(object sender, RoutedEventArgs e)
    {
        var notes = _host.Notes;
        if (notes.ActiveGroup is not { } group) return;

        // An open rename box is about to be renaming a list that no longer
        // exists, and would report the clash as the user's mistake.
        HideGroupEditor();

        var held = notes.Count(group);
        var home = notes.Groups.First(g => !string.Equals(g, group, StringComparison.Ordinal));

        // The notes are not what is being deleted here, so say where they go.
        var question = held == 0
            ? $"Delete the list \u201c{group}\u201d?"
            : $"Delete the list \u201c{group}\u201d?\n\n"
              + $"Its {held} note{(held == 1 ? string.Empty : "s")} move to {home}.";

        if (!Confirm(question)) return;

        notes.RemoveGroup(group);
        SaveNotes();
        PaintNotes();
    }

    private void OnNoteGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Row<NoteRow>(sender) is not { } row) return;
        if ((sender as ComboBox)?.SelectedItem is not string target) return;

        // Fires once as the template binds its initial value; that is not a move.
        if (!_host.Notes.MoveTo(row.Id, target)) return;

        SaveNotes();
        PaintNotes();
    }

    private void OnAddNote(object sender, RoutedEventArgs e)
    {
        if (_host.Notes.Add(NoteEntry.Text) is null) return;

        NoteEntry.Clear();

        // A new note that doesn't match the current filter would look like it
        // vanished, so the filter gets out of the way rather than the note.
        NoteFilter.Clear();

        SaveNotes();
        PaintNotes();
        NoteEntry.Focus();
    }

    /// <summary>Enter files the note; Shift+Enter is a second line.</summary>
    private void OnNoteEntryKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Return) return;
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return;

        e.Handled = true;
        OnAddNote(sender, e);
    }

    private void OnNoteFilterChanged(object sender, TextChangedEventArgs e) => PaintNotes();

    /// <summary>
    /// Hand the wheel back to the page once the note list has nothing left to
    /// scroll. A nested ScrollViewer otherwise swallows the wheel at its own
    /// end, and the window appears to jam whenever the pointer is over the
    /// first panel on it.
    /// </summary>
    private void OnNoteScrollWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer view) return;

        var stuck = view.ScrollableHeight <= 0
                    || (e.Delta > 0 && view.VerticalOffset <= 0)
                    || (e.Delta < 0 && view.VerticalOffset >= view.ScrollableHeight);
        if (!stuck) return;

        e.Handled = true;
        (view.Parent as UIElement)?.RaiseEvent(
            new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = view,
            });
    }

    private void OnNoteTextChanged(object sender, TextChangedEventArgs e)
    {
        if (Row<NoteRow>(sender) is not { } row || sender is not TextBox box) return;

        if (!_host.Notes.SetText(row.Id, box.Text)) return;

        _noteEdits = true;
        _host.NoteChanged(row.Id);
    }

    /// <summary>
    /// Moving off a note commits it — but does not redraw the list. A rebuild
    /// here would destroy the button you are on your way to clicking, between
    /// the mouse going down and the click arriving.
    /// </summary>
    private void OnNoteBlur(object sender, RoutedEventArgs e) => CommitNotes();

    private void CommitNotes()
    {
        if (!_noteEdits) return;

        _noteEdits = false;
        SaveNotes();
    }

    /// <summary>
    /// Write, prune and forget: the app is closing or the window is going away,
    /// so an emptied note has nowhere left to be typed back into.
    /// </summary>
    private void CommitAndPrune()
    {
        CommitNotes();
        if (_host.Notes.Prune() > 0) SaveNotes();
    }

    private void OnNoteDone(object sender, RoutedEventArgs e)
    {
        if (Row<NoteRow>(sender) is not { } row) return;

        _host.Notes.SetDone(row.Id, (sender as CheckBox)?.IsChecked == true);
        SaveNotes();
        PaintNotes();
    }

    private void OnNoteStick(object sender, RoutedEventArgs e)
    {
        if (Row<NoteRow>(sender) is not { } row) return;

        CommitNotes();
        _host.StickToDesktop(row.Id, !row.Stuck);
        PaintNotes();
    }

    private void OnNotePin(object sender, RoutedEventArgs e)
    {
        if (Row<NoteRow>(sender) is not { } row) return;

        _host.Notes.SetPinned(row.Id, !row.Pinned);
        SaveNotes();
        PaintNotes();
    }

    private void OnNoteDelete(object sender, RoutedEventArgs e)
    {
        if (Row<NoteRow>(sender) is not { } row) return;

        var preview = row.Text.Length > 60 ? row.Text[..60] + "…" : row.Text;
        if (!Confirm($"Delete this note?\n\n{preview}")) return;

        // Take it off the desktop first, or its window outlives it and edits a
        // note that no longer exists.
        if (row.Stuck) _host.StickToDesktop(row.Id, false);
        _host.Notes.Remove(row.Id);
        SaveNotes();
        PaintNotes();
    }

    private void OnClearDoneNotes(object sender, RoutedEventArgs e)
    {
        var group = _host.Notes.ActiveGroup;
        var done = _host.Notes.DoneCount(group);
        if (done == 0) return;

        var where = group is null ? "every list" : group;
        if (!Confirm($"Delete {done} finished note{(done == 1 ? string.Empty : "s")} in {where}?")) return;

        foreach (var note in _host.Notes.All(group).Where(n => n is { Done: true, Stuck: true }))
            _host.StickToDesktop(note.Id, false);

        _host.Notes.ClearDone(group);
        SaveNotes();
        PaintNotes();
    }

    private void OnOpenNotesFile(object sender, RoutedEventArgs e)
    {
        CommitNotes();

        try
        {
            if (!File.Exists(_host.Notes.Path_)) _host.Notes.Save();
            Process.Start(new ProcessStartInfo(_host.Notes.Path_) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Say($"Could not open the notes file: {ex.Message}");
        }
    }

    /// <summary>Write, and say so if the disk refused — silence would read as saved.</summary>
    private void SaveNotes()
    {
        if (!_host.Notes.Save() && _host.Notes.LastError is { } problem)
            Say($"Could not save notes: {problem}");
    }

    // ---------- local ports ----------

    /// <summary>
    /// Ask for a sweep and paint whatever comes back.
    ///
    /// Deliberately not awaited by the caller: the tick that drives this window
    /// runs on the UI thread, and a TCP table walk plus a process lookup per
    /// listener is not something to do there — that is exactly how "Check now"
    /// used to freeze the window for a moment on a busy machine.
    /// </summary>
    private async void RefreshPorts(bool fresh = false)
    {
        if (_scanning) return;

        _scanning = true;
        try
        {
            _ports = await _host.PortsAsync(fresh);
        }
        catch (Exception ex)
        {
            // The sweep is a nicety; the rest of the window is not.
            Debug.WriteLine($"[CoderMascot] port sweep failed: {ex.Message}");
            return;
        }
        finally
        {
            _scanning = false;
        }

        if (IsVisible) PaintPorts();
    }

    private void PaintPorts()
    {
        var shown = LocalPorts.Visible(_ports, PortFilter.Text, SystemPortsBox.IsChecked == true);

        PortList.ItemsSource = shown.Select(l => new PortRow(
            Port: $":{l.Port}",
            Title: $"{l.Process}  ·  pid {l.Pid}",
            Sub: $"{l.Where} · up {Friendly.Duration(l.Uptime.TotalSeconds)}",

            // Yours in green, the machine's own in grey. The question this panel
            // answers is "what have I got running", and everything else on a
            // Windows box is noise you are scrolling past.
            Pip: Freeze(l.Mine ? Tone.Live : Tone.Faint),
            Number: l.Port,
            Pid: l.Pid)).ToList();

        PortsEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PortsEmpty.Text = _ports.Count == 0
            ? "Nothing is listening on this machine."
            : PortFilter.Text.Trim().Length > 0
                ? $"Nothing listening matches \u201c{PortFilter.Text.Trim()}\u201d."
                : "Nothing above port 1024 is listening — tick System ports to see the rest.";

        _portCount = shown.Count;
        PortsCount.Text = LocalPorts.Summarise(shown, _ports.Count);
    }

    private void OnPortFilterChanged(object sender, RoutedEventArgs e) => PaintPorts();

    private void OnOpenPort(object sender, RoutedEventArgs e)
    {
        if (Row<PortRow>(sender) is not { } row) return;

        if (DesktopActions.OpenLocalPort(row.Number) is { } problem) Say(problem);
    }

    private void OnClosePort(object sender, RoutedEventArgs e)
    {
        if (Row<PortRow>(sender) is not { } row) return;

        if (!Confirm($"Ask whatever is on port {row.Number} to close?\n\n{row.Title}\n\n"
                     + "This is the same as clicking its X — anything unsaved will still prompt."))
            return;

        if (ProcessControl.Close(row.Pid) is { } problem) Say(problem);
        RefreshPorts(fresh: true);
    }

    // ---------- settings ----------

    /// <summary>
    /// Repaint the notes after they were changed somewhere else — on the
    /// desktop, usually.
    ///
    /// Skipped while the caret is inside one of these rows: rebuilding them
    /// under an editing cursor is exactly the keystroke-eating this panel is
    /// careful to avoid, and the row being typed into is already current.
    /// </summary>
    public void RefreshNotes()
    {
        if (!IsVisible || NoteList.IsKeyboardFocusWithin) return;

        PaintNotes();
    }

    /// <summary>Re-read the settings strip after something else changed them.</summary>
    public void RefreshSettings()
    {
        if (IsVisible) LoadSettings();
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var cfg = _host.Config;
            MessagesBox.SelectedIndex = AlertGate.Parse(cfg.Notifications) switch
            {
                AlertPolicy.Quiet => 1,
                AlertPolicy.Off => 2,
                _ => 0,
            };
            AnnounceBox.IsChecked = cfg.AnnounceFinished;

            // Both halves have to agree before this is ticked: the setting is
            // the intent, the Run entry is the thing that actually happens.
            StartupBox.IsChecked = cfg.StartWithWindows && StartupRegistration.IsEnabled();
            CpuLimitBox.Text = cfg.CpuWarnPercent.ToString("0", CultureInfo.InvariantCulture);
            MemLimitBox.Text = cfg.MemoryWarnPercent.ToString("0", CultureInfo.InvariantCulture);
            WsIdleBox.Text = cfg.WorkspaceIdleMinutes.ToString(CultureInfo.InvariantCulture);
            DevIdleBox.Text = cfg.DevServerIdleMinutes.ToString(CultureInfo.InvariantCulture);
            SettingsNote.Text = string.Empty;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void OnMessagesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;

        _host.SetAlertPolicy(MessagesBox.SelectedIndex switch
        {
            1 => AlertPolicy.Quiet,
            2 => AlertPolicy.Off,
            _ => AlertPolicy.All,
        });
    }

    private void OnAnnounceChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;

        _host.Config.AnnounceFinished = AnnounceBox.IsChecked == true;
        _host.Config.Save();
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;

        var on = StartupBox.IsChecked == true;
        if (_host.SetStartWithWindows(on) is not { } problem) return;

        StartupBox.IsChecked = !on;
        Say($"Could not change the startup setting: {problem}");
    }

    private void OnApplySettings(object sender, RoutedEventArgs e)
    {
        var cfg = _host.Config;

        if (Num(CpuLimitBox.Text) is { } cpu) cfg.CpuWarnPercent = cpu;
        if (Num(MemLimitBox.Text) is { } mem) cfg.MemoryWarnPercent = mem;
        if (Num(WsIdleBox.Text) is { } wsIdle) cfg.WorkspaceIdleMinutes = (int)wsIdle;
        if (Num(DevIdleBox.Text) is { } devIdle) cfg.DevServerIdleMinutes = (int)devIdle;

        // Clamp through the same rules Load() applies, so a value typed here
        // can't reach a place a value read from disk could never reach.
        cfg.CpuWarnPercent = Math.Clamp(cfg.CpuWarnPercent, 40, 99);
        cfg.MemoryWarnPercent = Math.Clamp(cfg.MemoryWarnPercent, 40, 99);
        cfg.WorkspaceIdleMinutes = cfg.WorkspaceIdleMinutes <= 0 ? 0 : Math.Clamp(cfg.WorkspaceIdleMinutes, 5, 10080);
        cfg.DevServerIdleMinutes = cfg.DevServerIdleMinutes <= 0 ? 0 : Math.Clamp(cfg.DevServerIdleMinutes, 5, 10080);

        cfg.Save();
        _host.SettingsChanged();

        LoadSettings();
        SettingsNote.Text = "Saved.";
    }

    /// <summary>
    /// A number typed into the settings strip. Invariant first, then whatever
    /// this machine calls a decimal point — the app writes "88.5" but a keyboard
    /// in a comma locale types "88,5", and refusing that reads as the box being
    /// broken.
    /// </summary>
    private static double? Num(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v) ? v : null;
    }

    private void OnEditConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CoderConfig.Dir);
            if (!File.Exists(CoderConfig.Path_)) _host.Config.Save();
            Process.Start(new ProcessStartInfo(CoderConfig.Path_) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Say($"Could not open config: {ex.Message}");
        }
    }

    // ---------- plumbing ----------

    /// <summary>The row behind a button inside a DataTemplate.</summary>
    private static T? Row<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.DataContext as T;

    private void Say(string message) =>
        MessageBox.Show(this, message, "Coder Mascot", MessageBoxButton.OK, MessageBoxImage.Information);

    private bool Confirm(string question) =>
        MessageBox.Show(this, question, "Coder Mascot",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
}
