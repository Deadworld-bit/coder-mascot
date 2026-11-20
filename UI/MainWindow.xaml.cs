using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    /// <summary>
    /// The connect form saved a new address and token. The token is read per
    /// request, so it is already in use; this is for what would otherwise wait
    /// for the next poll to notice.
    /// </summary>
    void Reconnected();

    /// <summary>What the Projects page reads and edits: the repositories, their readings and their folds.</summary>
    IBranchHost Branches { get; }

    /// <summary>What the Logs page reads and edits: the log sources and their readings.</summary>
    ILogHost Logs { get; }

    /// <summary>Branches, across every repository, with work that has not reached every deploy target.</summary>
    int OutstandingBranches { get; }
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
public sealed record GroupTab(string? Name, string Label, string Open, Visibility OpenShown,
                              bool Selected, Brush Ink, FontWeight Weight, Brush Bar);
public sealed record PortRow(string Port, string Title, string Sub, Brush Pip, int Number, int Pid);

/// <summary>
/// One stop in the left nav, and how much is behind it.
///
/// A live object, unlike the rows beside it: the buttons are built once and
/// repainted in place. Rebuilt every second they would drop keyboard focus off
/// whichever one you had tabbed to.
/// </summary>
public sealed class NavEntry : INotifyPropertyChanged
{
    private readonly string _tip;
    private bool _selected;
    private bool _urgent;
    private bool _collapsed;
    private int _count;

    public NavEntry(string key, string title, string tip, Geometry icon)
    {
        Key = key;
        Title = title;
        Icon = icon;
        _tip = tip;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }
    public string Title { get; private set; }
    public Geometry Icon { get; private set; }

    public bool Selected => _selected;
    public string Badge => _count.ToString(CultureInfo.InvariantCulture);

    public Brush Ink => Palette.Of(_selected ? Tone.Ink : Tone.Faint);
    public Brush Bar => _selected ? Palette.Of(Tone.Pick) : System.Windows.Media.Brushes.Transparent;
    public FontWeight Weight => _selected ? FontWeights.SemiBold : FontWeights.Normal;
    public Brush BadgeBack => Palette.Of(_urgent ? Tone.BadEdge : Tone.Lift);
    public Brush BadgeInk => Palette.Of(_urgent ? Tone.BadInk : Tone.Ink2);

    public Visibility LabelShown => _collapsed ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BadgeShown => _count > 0 && !_collapsed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DotShown => _count > 0 && _urgent && _collapsed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The name goes on the tip once the label is gone; with no tip, it is all there is.</summary>
    public string? Hover => _tip.Length == 0
        ? _collapsed ? Title : null
        : _collapsed ? $"{Title} \u2014 {_tip}" : _tip;

    /// <summary>Repaint; tells the buttons only when something actually differs.</summary>
    public void Set(bool selected, int count, bool urgent, bool collapsed)
    {
        if (_selected == selected && _count == count && _urgent == urgent && _collapsed == collapsed) return;

        _selected = selected;
        _count = count;
        _urgent = urgent;
        _collapsed = collapsed;
        Changed();
    }

    /// <summary>For the collapse toggle, which turns into the expand one.</summary>
    public void Relabel(string title, Geometry icon)
    {
        Title = title;
        Icon = icon;
        Changed();
    }

    // An empty name means every property, so the button follows in one notification.
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// One row of the Settings index, and the heading it jumps to. A live object like
/// the nav's, so scrolling repaints a row in place instead of rebuilding the
/// buttons under the keyboard.
/// </summary>
public sealed class SettingsSection : INotifyPropertyChanged
{
    private bool _selected;

    public SettingsSection(string title, FrameworkElement heading)
    {
        Title = title;
        Heading = heading;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }
    public FrameworkElement Heading { get; }

    public bool Selected => _selected;
    public Brush Ink => Palette.Of(_selected ? Tone.Ink : Tone.Ink2);
    public Brush Bar => _selected ? Palette.Of(Tone.Pick) : System.Windows.Media.Brushes.Transparent;
    public FontWeight Weight => _selected ? FontWeights.SemiBold : FontWeights.Normal;

    public void Select(bool on)
    {
        if (_selected == on) return;

        _selected = on;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>
/// The main window: a left nav and the pages behind it, which between them hold
/// everything the badge cannot say in one colour.
///
/// Deliberately pull-based on a timer rather than pushed at from the monitors.
/// Most of what it shows is elapsed time — "thinking 4 min", "up 6h" — which
/// changes every second without any underlying event, so a push model would
/// either need a fake event every second or show stale numbers. Nothing here
/// polls anything remote; it re-reads the snapshots the app already holds.
/// </summary>
public partial class MainWindow : Window, IDrawerHost
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

    /// <summary>
    /// What the Settings page was loaded with: each control is compared with its
    /// own field to tell an edit from the way it already was. The numbers are the
    /// text the boxes were given, because that is what an untouched box holds.
    /// </summary>
    private readonly record struct Saved(
        AlertPolicy Messages, bool Announce, bool Startup,
        string Cpu, string Mem, string WsIdle, string DevIdle);

    private Saved _saved;

    /// <summary>How many controls differ from <see cref="_saved"/>; the nav badge shows it.</summary>
    private int _pending;

    /// <summary>"Saved." stays on the Apply bar for a moment after an Apply.</summary>
    private bool _showSaved;
    private readonly DispatcherTimer _savedTimer;

    private bool _closingForShutdown;

    /// <summary>The close question: on screen now, already answered, or past caring because the window has gone.</summary>
    private bool _asking;
    private bool _closeAnswered;
    private bool _closed;

    private readonly SettingsSection[] _sections;
    private SettingsSection _sectionPicked;

    /// <summary>Where a click on the index left the Settings scroller; NaN until the first click.</summary>
    private double _sectionAt = double.NaN;

    /// <summary>Which page is showing. Overview, because that is what the app is for.</summary>
    private string _page = "Overview";

    private const double NavWide = 208;
    private const double NavNarrow = 56;

    /// <summary>The Overview page is two columns from this width up, one below it.</summary>
    private const double OverviewTwoColumns = 860;

    /// <summary>Below this window width the nav folds to icons, so the pages keep their room.</summary>
    private const double NavFoldBelow = 1000;

    /// <summary>
    /// Below this page width the lists pane narrows, so what is beside it stays readable.
    /// The Notes lists, the Projects repositories and the Settings index use the same line.
    /// </summary>
    internal const double NotesListsWide = 760;

    private bool _windowWide = true;

    private readonly NavEntry _navOverview;
    private readonly NavEntry _navProjects;
    private readonly NavEntry _navLogs;
    private readonly NavEntry _navNotes;
    private readonly NavEntry _navPorts;
    private readonly NavEntry _navSettings;
    private readonly NavEntry _navToggle;

    private readonly ProjectsPage _projects;
    private readonly LogsPage _logs;

    /// <summary>How far the drawer travels as it arrives: a nudge with a fade, not a swipe.</summary>
    private const double DrawerSlide = 40;

    /// <summary>What the drawer always leaves free on the left, so the window is still visible behind it.</summary>
    private const double DrawerGutter = 56;

    /// <summary>The form in the drawer, or null when it is shut. One at a time.</summary>
    private IDrawerForm? _drawer;
    private TaskCompletionSource<bool>? _drawerDone;

    /// <summary>What had the keyboard when the drawer opened, so closing it puts the focus back.</summary>
    private IInputElement? _drawerReturn;

    /// <summary>In memory only: the window opens expanded every time.</summary>
    private bool _navCollapsed;

    private bool _overviewWide = true;

    /// <summary>How many rows the last paint put on each page, for the badges.</summary>
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

    public MainWindow(IDashboardHost host)
    {
        _host = host;
        InitializeComponent();
        DarkChrome.Apply(this);

        // Built here but not started: it reads nothing until it is first shown.
        _projects = new ProjectsPage(host.Branches, this);
        PageProjects.Child = _projects;

        // Reads nothing until the page is shown, but listens from now on, so a log
        // that changes behind another page only marks it for a repaint.
        _logs = new LogsPage(host.Logs, this);
        PageLogs.Child = _logs;

        _navOverview = new NavEntry("Overview", "Overview",
            "Workspace, sessions, this machine, and what's been left running", Glyph("IconOverview"));
        _navProjects = new NavEntry("Projects", "Projects",
            "Where work has got to, across every repository", Glyph("IconProjects"));
        _navLogs = new NavEntry("Logs", "Logs", "Log files you have pasted into folders on this PC", Glyph("IconLogs"));
        _navNotes = new NavEntry("Notes", "Notes", "Your notes, and the lists they are kept in", Glyph("IconNotes"));
        _navPorts = new NavEntry("Ports", "Ports", "What is listening on this machine", Glyph("IconPorts"));
        _navSettings = new NavEntry("Settings", "Settings",
            "How loud the mascot is, and what it watches", Glyph("IconSettings"));
        _navToggle = new NavEntry("Collapse", "Collapse", string.Empty, Glyph("IconCollapse"));

        NavList.ItemsSource = new[] { _navOverview, _navProjects, _navLogs, _navNotes, _navPorts };
        NavFoot.ItemsSource = new[] { _navSettings, _navToggle };

        _sections = [new("Messages", HeadMessages), new("Startup", HeadStartup),
                     new("Warnings", HeadWarnings), new("Config file", HeadConfig)];
        _sectionPicked = _sections[0];
        SectionIndex.ItemsSource = _sections;
        PaintSectionIndex();

        _savedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _savedTimer.Tick += (_, _) =>
        {
            _savedTimer.Stop();
            _showSaved = false;
            UpdateApplyBar();
        };

        // Now rather than at first show: the nav badge and the question on closing
        // both need the baseline, and the first page shown may not be this one.
        LoadSettings();

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
            if ((bool)e.NewValue) { RefreshSettings(); PaintNotes(); Refresh(); RefreshPorts(fresh: true); ShowPage(_page); _tick.Start(); }
            else { _tick.Stop(); CommitAndPrune(); }
        };

        // Notes are the one thing here that isn't re-derivable from a poll, so
        // they get written on the way out as well as after every action.
        Closed += (_, _) =>
        {
            _closed = true;
            _savedTimer.Stop();

            // Whoever is awaiting the form gets an answer, and the form stops its work.
            CloseDrawer(false, finished: false);
            _projects.Stop();
            _logs.Stop();
            CommitAndPrune();
        };

        // Preview, so the Projects page's shortcuts work wherever the focus is.
        PreviewKeyDown += OnWindowKey;

        // Not preview: a dropdown that is open takes the first Escape.
        KeyDown += OnWindowKeyDown;

        // A logoff must not wait on the question about unapplied settings.
        if (System.Windows.Application.Current is { } app)
        {
            SessionEndingCancelEventHandler ending = (_, _) => _closingForShutdown = true;
            app.SessionEnding += ending;
            Closed += (_, _) => app.SessionEnding -= ending;
        }

        // CenterScreen does not shrink a window to fit, so on a small or scaled
        // screen the title bar would open above the top edge.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);

        SizeChanged += OnWindowSized;
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
        // page, and focusing something nobody can see is worse than useless.
        ShowPage("Notes");

        // After layout, or the caret lands in a box that is still zero-sized.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NoteEntry.Focus());
    }

    /// <summary>Show it with the Projects page up.</summary>
    public void ShowProjects()
    {
        // Before the window appears, so a window that is not up yet opens on
        // this page instead of flashing the Overview first.
        ShowPage("Projects");
        Summon();
    }

    /// <summary>
    /// Show the window, whatever page it is on, and open the connect form in the
    /// drawer. If a form is already open — this one or another — it is left
    /// alone: two at once could save different credentials over each other.
    /// </summary>
    public void ShowConnect()
    {
        Summon();

        if (_drawer is ConnectForm) return;
        if (_drawer is not null)
        {
            // Otherwise the request lands on someone else's form with no word as to why.
            DrawerSub.Text = "To connect to Coder, finish or close this form first.";
            DrawerSub.Visibility = Visibility.Visible;
            return;
        }

        var form = new ConnectForm(_host.Config);
        form.Connected += (_, _) => _host.Reconnected();
        _ = OpenAsync(form);
    }

    // ---------- drawer ----------

    public Task<bool> OpenAsync(IDrawerForm form)
    {
        if (form is not UIElement)
            throw new ArgumentException("A drawer form has to be a UIElement.", nameof(form));

        if (_drawer is not null)
        {
            Summon();
            return Task.FromResult(false);
        }

        DrawerTitle.Text = form.Heading;
        DrawerSub.Text = form.Subheading ?? string.Empty;
        DrawerSub.Visibility = string.IsNullOrWhiteSpace(form.Subheading) ? Visibility.Collapsed : Visibility.Visible;
        DrawerPanel.Width = form.PreferredWidth;
        FitDrawer();

        // Claimed only once the form is in: a form that can't be hosted must not
        // leave the drawer looking busy for good.
        DrawerBody.Content = form;

        _drawer = form;
        _drawerDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _drawerReturn = System.Windows.Input.Keyboard.FocusedElement;
        form.Finished += OnDrawerFinished;

        Shell.IsEnabled = false;
        DrawerLayer.Visibility = Visibility.Visible;
        AnimateDrawerIn();

        // After the form has loaded, or there is nothing yet to put the caret in.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ReferenceEquals(_drawer, form)) form.FocusFirst();
        });

        return _drawerDone.Task;
    }

    private void OnDrawerFinished(object? sender, bool done)
    {
        if (ReferenceEquals(sender, _drawer)) CloseDrawer(done, finished: true);
    }

    /// <summary>
    /// Shut the drawer. <paramref name="finished"/> is the form's own doing; for
    /// anything else (Escape, the close button, the window going) the form is
    /// told to stop what it is doing.
    /// </summary>
    private void CloseDrawer(bool result, bool finished)
    {
        if (_drawer is not { } form) return;

        var done = _drawerDone;
        var back = _drawerReturn;
        _drawer = null;
        _drawerDone = null;
        _drawerReturn = null;

        form.Finished -= OnDrawerFinished;

        try
        {
            if (!finished) form.Abandon();
        }
        finally
        {
            // Whatever the form did on the way out, the window must come back.
            DrawerLayer.Visibility = Visibility.Collapsed;
            DrawerBody.Content = null;
            Shell.IsEnabled = true;

            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Refocus(back));

            done?.TrySetResult(result);
        }
    }

    /// <summary>
    /// Back to what had the keyboard — unless saving rebuilt it (a diagram chip, a
    /// repository row) or it sat in a menu that has closed; then the page itself.
    /// </summary>
    private void Refocus(IInputElement? back)
    {
        if (back?.Focus() == true) return;

        UIElement page = _page switch
        {
            "Projects" => PageProjects,
            "Logs" => PageLogs,
            "Notes" => PageNotes,
            "Ports" => PagePorts,
            "Settings" => PageSettings,
            _ => PageOverview,
        };
        page.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }

    private void OnDrawerClose(object sender, RoutedEventArgs e) => CloseDrawer(false, finished: false);

    private void OnClientSized(object sender, SizeChangedEventArgs e) => FitDrawer();

    /// <summary>Never wider than the window less a margin. Until the first layout there is no width to go by.</summary>
    private void FitDrawer()
    {
        if (Client.ActualWidth > 0) DrawerPanel.MaxWidth = Math.Max(0, Client.ActualWidth - DrawerGutter);
    }

    private void AnimateDrawerIn()
    {
        var span = TimeSpan.FromMilliseconds(150);

        // Stop, not hold: the resting values are already the final ones.
        var fade = new DoubleAnimation(0, 1, span) { FillBehavior = FillBehavior.Stop };
        var slide = new DoubleAnimation(DrawerSlide, 0, span)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };

        DrawerScrim.BeginAnimation(OpacityProperty, fade);
        DrawerPanel.BeginAnimation(OpacityProperty, fade);
        DrawerShift.BeginAnimation(TranslateTransform.XProperty, slide);
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
        PaintNav();
    }

    // ---------- nav ----------

    /// <summary>
    /// The nav, with a count on each item.
    ///
    /// The counts are the point. Behind a page, a thing that wants attention is
    /// invisible — which is how a dev server left running for two days goes
    /// unnoticed — so the number comes out to where it can be seen without
    /// opening anything.
    /// </summary>
    private void PaintNav()
    {
        Put(_navOverview, _leftoverCount, urgent: _leftoverCount > 0);
        Put(_navProjects, _host.OutstandingBranches);
        Put(_navLogs, LogErrors());
        Put(_navNotes, _noteCount);
        Put(_navPorts, _portCount);
        Put(_navSettings, _pending);
        Put(_navToggle, 0);
    }

    /// <summary>Errors and fatals across every log folder: a count, not an alarm, so it is not urgent.</summary>
    private int LogErrors() => _host.Logs.Reader.Views.Sum(v => v.Errors);

    private void Put(NavEntry item, int count, bool urgent = false) =>
        item.Set(_page == item.Key, count, urgent, _navCollapsed);

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (Row<NavEntry>(sender) is not { } item) return;

        switch (item.Key)
        {
            case "Collapse": ToggleNav(); break;
            default: ShowPage(item.Key); break;
        }
    }

    private void ToggleNav()
    {
        _navCollapsed = !_navCollapsed;
        NavRail.Width = _navCollapsed ? NavNarrow : NavWide;
        _navToggle.Relabel(_navCollapsed ? "Expand" : "Collapse",
                           Glyph(_navCollapsed ? "IconExpand" : "IconCollapse"));
        PaintNav();
    }

    /// <summary>
    /// Fold the nav when the window gets narrow and open it again when it widens.
    /// Only on crossing the line, so a manual toggle in between is left alone.
    /// </summary>
    private void OnWindowSized(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= NavFoldBelow;
        if (wide == _windowWide) return;

        _windowWide = wide;
        if (_navCollapsed == wide) ToggleNav();
    }

    private void OnNotesSized(object sender, SizeChangedEventArgs e) =>
        NotesListsColumn.Width = new GridLength(e.NewSize.Width >= NotesListsWide ? 240 : 180);

    /// <summary>
    /// Keys for the Projects page while it is showing, whatever has the focus, and
    /// Ctrl+S to apply on Settings. Escape is not ours to take: nothing here closes
    /// the window. Not while a drawer is open either: the page is switched off, and
    /// Escape is the drawer's.
    /// </summary>
    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (_drawer is not null) return;

        if (_page == "Projects") _projects.OnShortcut(sender, e);
        else if (_page == "Logs") _logs.OnShortcut(sender, e);
        else if (_page == "Settings" && _pending > 0 && e.Key == System.Windows.Input.Key.S
                 && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            e.Handled = true;
            ApplySettings();
        }
    }

    /// <summary>Escape shuts the drawer, as Cancel does.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_drawer is null || e.Key != System.Windows.Input.Key.Escape) return;

        e.Handled = true;
        CloseDrawer(false, finished: false);
    }

    private void ShowPage(string key)
    {
        _page = key;

        PageOverview.Visibility = key == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        PageProjects.Visibility = key == "Projects" ? Visibility.Visible : Visibility.Collapsed;
        PageLogs.Visibility = key == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        PageNotes.Visibility = key == "Notes" ? Visibility.Visible : Visibility.Collapsed;
        PagePorts.Visibility = key == "Ports" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = key == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        PageTitle.Text = key;
        PageSub.Text = key switch
        {
            "Overview" => "What the mascot is watching right now.",
            "Projects" => "Where work has got to, across every repository.",
            "Logs" => "Log files pasted into folders on this PC \u2014 read here, never sent anywhere.",
            "Notes" => "Your notes, and the lists they are kept in.",
            "Ports" => "What is listening on this machine right now.",
            "Settings" => "How the mascot reaches Coder, how much it interrupts you, and when it warns.",
            _ => string.Empty,
        };

        PaintNav();
    }

    private Geometry Glyph(string key) => (Geometry)FindResource(key);

    /// <summary>
    /// Two columns while there is room, one in reading order when there is not:
    /// the right-hand sections move under the left-hand ones, and the workspace
    /// buttons drop under its facts.
    /// </summary>
    private void OnOverviewSized(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= OverviewTwoColumns;
        if (wide == _overviewWide) return;

        _overviewWide = wide;

        var home = wide ? OverviewRight : OverviewLeft;
        foreach (var section in new[] { SectionMachine, SectionRecent })
        {
            (section.Parent as StackPanel)?.Children.Remove(section);
            home.Children.Add(section);
        }

        OverviewRight.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        OverviewRightColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        Grid.SetRow(WorkspaceActions, wide ? 0 : 1);
        Grid.SetColumn(WorkspaceActions, wide ? 1 : 0);
        Grid.SetColumnSpan(WorkspaceActions, wide ? 1 : 2);
        WorkspaceActions.Margin = wide ? new Thickness(16, 0, 0, 0) : new Thickness(0, 12, 0, 0);
    }

    private static string Headline(WorkspaceSnapshot? ws)
    {
        var state = ws?.State ?? MascotState.Unknown;

        return ws?.WorkspaceName is { Length: > 0 } n
            ? $"{state.Title()} — {n}"
            : state.Title();
    }

    private void PaintHeader(WorkspaceSnapshot? ws)
    {
        StatePip.Fill = Pip(ws?.State ?? MascotState.Unknown);
        HeadTitle.Text = Headline(ws);
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
        WorkspacePip.Fill = Pip(ws?.State ?? MascotState.Unknown);
        WorkspaceTitle.Text = Headline(ws);

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

    private void OnShowSetup(object sender, RoutedEventArgs e) => ShowConnect();

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

    /// <summary>One row per list, with what's still open in it, and All in front.</summary>
    private void PaintGroupTabs(NoteBook notes, string? active)
    {
        var tabs = new List<GroupTab> { ListTab(null, "All", active is null, notes.OpenCount()) };

        foreach (var name in notes.Groups)
            tabs.Add(ListTab(name, name, string.Equals(name, active, StringComparison.Ordinal),
                             notes.OpenCount(name)));

        GroupTabs.ItemsSource = tabs;
    }

    private static GroupTab ListTab(string? name, string label, bool selected, int open) => new(
        Name: name,
        Label: label,
        Open: open.ToString(CultureInfo.InvariantCulture),
        OpenShown: open > 0 ? Visibility.Visible : Visibility.Collapsed,
        Selected: selected,
        Ink: Freeze(selected ? Tone.Ink : Tone.Ink2),
        Weight: selected ? FontWeights.SemiBold : FontWeights.Normal,
        Bar: selected ? Freeze(Tone.Pick) : System.Windows.Media.Brushes.Transparent);

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

    /// <summary>
    /// Re-read the settings after something else changed them — a menu, or Connect.
    /// A control that still holds what it was loaded with takes the new value; one
    /// with an edit in it is left alone, so nothing typed here is overwritten.
    /// </summary>
    public void RefreshSettings()
    {
        var was = _saved;
        var now = ReadSaved();

        _loadingSettings = true;
        try
        {
            if (!MessagesDiffer(was)) ShowMessages(now.Messages);
            if (!AnnounceDiffers(was)) AnnounceBox.IsChecked = now.Announce;
            if (!StartupDiffers(was)) StartupBox.IsChecked = now.Startup;
            if (!NumberDiffers(CpuLimitBox, was.Cpu)) CpuLimitBox.Text = now.Cpu;
            if (!NumberDiffers(MemLimitBox, was.Mem)) MemLimitBox.Text = now.Mem;
            if (!NumberDiffers(WsIdleBox, was.WsIdle)) WsIdleBox.Text = now.WsIdle;
            if (!NumberDiffers(DevIdleBox, was.DevIdle)) DevIdleBox.Text = now.DevIdle;
        }
        finally
        {
            _loadingSettings = false;
        }

        _saved = now;
        SettingsEdited();
    }

    /// <summary>Put the saved values in every control, whatever they held.</summary>
    private void LoadSettings()
    {
        _saved = ReadSaved();

        _loadingSettings = true;
        try
        {
            ShowMessages(_saved.Messages);
            AnnounceBox.IsChecked = _saved.Announce;
            StartupBox.IsChecked = _saved.Startup;
            CpuLimitBox.Text = _saved.Cpu;
            MemLimitBox.Text = _saved.Mem;
            WsIdleBox.Text = _saved.WsIdle;
            DevIdleBox.Text = _saved.DevIdle;
        }
        finally
        {
            _loadingSettings = false;
        }

        SettingsEdited();
    }

    private Saved ReadSaved()
    {
        var cfg = _host.Config;

        return new Saved(
            AlertGate.Parse(cfg.Notifications),
            cfg.AnnounceFinished,

            // Both halves have to agree before this is ticked: the setting is
            // the intent, the Run entry is the thing that actually happens.
            cfg.StartWithWindows && StartupRegistration.IsEnabled(),
            cfg.CpuWarnPercent.ToString("0", CultureInfo.InvariantCulture),
            cfg.MemoryWarnPercent.ToString("0", CultureInfo.InvariantCulture),
            cfg.WorkspaceIdleMinutes.ToString(CultureInfo.InvariantCulture),
            cfg.DevServerIdleMinutes.ToString(CultureInfo.InvariantCulture));
    }

    private void ShowMessages(AlertPolicy policy) =>
        (policy switch { AlertPolicy.Quiet => MessagesQuiet, AlertPolicy.Off => MessagesOff, _ => MessagesAll })
            .IsChecked = true;

    private AlertPolicy ChosenMessages =>
        MessagesQuiet.IsChecked == true ? AlertPolicy.Quiet
        : MessagesOff.IsChecked == true ? AlertPolicy.Off
        : AlertPolicy.All;

    private bool MessagesDiffer(Saved was) => ChosenMessages != was.Messages;

    private bool AnnounceDiffers(Saved was) => (AnnounceBox.IsChecked == true) != was.Announce;

    private bool StartupDiffers(Saved was) => (StartupBox.IsChecked == true) != was.Startup;

    /// <summary>A box that is not a number holds something other than what it was given, so it differs too.</summary>
    private static bool NumberDiffers(TextBox box, string was) => Num(box.Text) is not { } v || v != Num(was);

    private int PendingCount() => new[]
    {
        MessagesDiffer(_saved), AnnounceDiffers(_saved), StartupDiffers(_saved),
        NumberDiffers(CpuLimitBox, _saved.Cpu), NumberDiffers(MemLimitBox, _saved.Mem),
        NumberDiffers(WsIdleBox, _saved.WsIdle), NumberDiffers(DevIdleBox, _saved.DevIdle),
    }.Count(differs => differs);

    private void OnSettingEdited(object sender, RoutedEventArgs e)
    {
        if (!_loadingSettings) SettingsEdited();
    }

    /// <summary>What follows from a control having changed: the lines under them, and the Apply bar.</summary>
    private void SettingsEdited()
    {
        MessagesNote.Text = ChosenMessages switch
        {
            AlertPolicy.Quiet => "No pop-ups; the mascot still comes to get you.",
            AlertPolicy.Off => "Badge colour only.",
            _ => "Pop-ups, and the mascot comes to get you.",
        };

        JudgeLimit(CpuLimitBox, CpuRange);
        JudgeLimit(MemLimitBox, MemRange);
        JudgeLimit(WsIdleBox, WsIdleRange);
        JudgeLimit(DevIdleBox, DevIdleRange);

        UpdateApplyBar();
    }

    /// <summary>The range under a box, until what is typed in it stops being a number.</summary>
    private static void JudgeLimit(TextBox box, TextBlock range)
    {
        // The range as written in the XAML, for when the box is right again.
        range.Tag ??= range.Text;

        if (Num(box.Text) is null)
        {
            range.Text = "not a number — it will stay as it was";
            range.Foreground = Palette.Of(Tone.BadInk);
        }
        else
        {
            range.Text = (string)range.Tag;
            range.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    /// <summary>
    /// Shown while something differs from what is saved, and for a moment after
    /// an Apply. The nav badge carries the same count to the other pages.
    /// </summary>
    private void UpdateApplyBar()
    {
        _pending = PendingCount();
        Put(_navSettings, _pending);

        if (_pending > 0) _showSaved = false;

        ApplyBar.Visibility = _pending > 0 || _showSaved ? Visibility.Visible : Visibility.Collapsed;
        ApplyActions.Visibility = _pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyText.Text = _pending switch
        {
            0 => "Saved.",
            1 => "1 change not applied",
            _ => $"{_pending} changes not applied",
        };
    }

    private void OnApplySettings(object sender, RoutedEventArgs e) => ApplySettings();

    private void OnDiscardSettings(object sender, RoutedEventArgs e)
    {
        ReleaseApplyFocus();
        LoadSettings();
    }

    /// <summary>
    /// Write every control that differs from what is saved, then reload so what
    /// shows is what was kept — a clamped number comes back as the number it was
    /// clamped to. A startup change that is refused stays pending, with the reason.
    /// </summary>
    private void ApplySettings()
    {
        var cfg = _host.Config;
        var was = _saved;

        var messages = MessagesDiffer(was);
        if (messages) _host.SetAlertPolicy(ChosenMessages);

        var announce = AnnounceDiffers(was);
        var numbers = false;

        if (announce) cfg.AnnounceFinished = AnnounceBox.IsChecked == true;

        if (NumberDiffers(CpuLimitBox, was.Cpu) && Num(CpuLimitBox.Text) is { } cpu)
        {
            cfg.CpuWarnPercent = cpu;
            numbers = true;
        }

        if (NumberDiffers(MemLimitBox, was.Mem) && Num(MemLimitBox.Text) is { } mem)
        {
            cfg.MemoryWarnPercent = mem;
            numbers = true;
        }

        if (NumberDiffers(WsIdleBox, was.WsIdle) && Num(WsIdleBox.Text) is { } wsIdle)
        {
            cfg.WorkspaceIdleMinutes = (int)wsIdle;
            numbers = true;
        }

        if (NumberDiffers(DevIdleBox, was.DevIdle) && Num(DevIdleBox.Text) is { } devIdle)
        {
            cfg.DevServerIdleMinutes = (int)devIdle;
            numbers = true;
        }

        if (announce || numbers)
        {
            // Clamp through the same rules Load() applies, so a value typed here
            // can't reach a place a value read from disk could never reach.
            cfg.CpuWarnPercent = Math.Clamp(cfg.CpuWarnPercent, 40, 99);
            cfg.MemoryWarnPercent = Math.Clamp(cfg.MemoryWarnPercent, 40, 99);
            cfg.WorkspaceIdleMinutes = cfg.WorkspaceIdleMinutes <= 0 ? 0 : Math.Clamp(cfg.WorkspaceIdleMinutes, 5, 10080);
            cfg.DevServerIdleMinutes = cfg.DevServerIdleMinutes <= 0 ? 0 : Math.Clamp(cfg.DevServerIdleMinutes, 5, 10080);

            cfg.Save();
            if (numbers) _host.SettingsChanged();
        }

        var startup = StartupBox.IsChecked == true;
        var startupDiffers = StartupDiffers(was);
        var problem = startupDiffers ? _host.SetStartWithWindows(startup) : null;

        ReleaseApplyFocus();
        LoadSettings();

        if (problem is not null)
        {
            StartupBox.IsChecked = startup;
            SettingsEdited();
            Say($"Could not change the startup setting: {problem}");
            return;
        }

        // Only a box that wasn't a number was pending: it has gone back, and nothing was written.
        if (!messages && !announce && !numbers && !startupDiffers) return;

        _showSaved = true;
        _savedTimer.Stop();
        _savedTimer.Start();
        UpdateApplyBar();
    }

    /// <summary>The bar's buttons are about to go; leave the keyboard on the page rather than on nothing.</summary>
    private void ReleaseApplyFocus()
    {
        if (ApplyBar.IsKeyboardFocusWithin)
            SettingsScroll.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }

    /// <summary>
    /// Closing with changes still pending asks once. At quit it does not:
    /// <see cref="CloseForShutdown"/>.
    ///
    /// The close is cancelled and the question asked afterwards, never from in
    /// here: a window is "closing" until this returns, and the tray, the hotkey
    /// and a logoff all keep running under a modal box — reopening or quitting
    /// meanwhile would throw, and a quit would skip saving the notes.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || _closingForShutdown || _closeAnswered || PendingCount() == 0) return;

        e.Cancel = true;
        if (_asking) return;

        _asking = true;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // Quit may have got here first; a closed owner would leave the box unowned and on its own.
                if (_closed || _closingForShutdown) return;

                var answer = MessageBox.Show(this, "Apply your settings changes before closing?", "Coder Mascot",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (_closed || _closingForShutdown || answer is MessageBoxResult.Cancel or MessageBoxResult.None) return;

                if (answer == MessageBoxResult.Yes)
                {
                    ApplySettings();

                    // A refused startup change stays pending, and so does the window.
                    if (PendingCount() > 0) return;
                }

                _closeAnswered = true;
                Close();
            }
            finally
            {
                _asking = false;
            }
        });
    }

    /// <summary>Close without the question: the app is quitting, and nobody is left to answer it.</summary>
    public void CloseForShutdown()
    {
        _closingForShutdown = true;

        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // Already closing. The question is never asked from inside a close, so this is only a backstop.
        }
    }

    // ---------- settings: the section index ----------

    private void OnSettingsSized(object sender, SizeChangedEventArgs e) =>
        SettingsIndexColumn.Width = new GridLength(e.NewSize.Width >= NotesListsWide ? 240 : 180);

    private void OnPickSection(object sender, RoutedEventArgs e)
    {
        if (Row<SettingsSection>(sender) is not { } section) return;

        _sectionPicked = section;

        // To the heading's own top edge, so it lands at the top and not merely in
        // view. The scroller stops at its end, which the last sections can reach
        // before their heading does; the row stays the one that was clicked.
        _sectionAt = SettingsScroll.ScrollableHeight < 1
            ? double.NaN
            : Math.Clamp(HeadingTop(section), 0, SettingsScroll.ScrollableHeight);

        if (!double.IsNaN(_sectionAt)) SettingsScroll.ScrollToVerticalOffset(_sectionAt);

        PaintSectionIndex();
    }

    private void OnSettingsScrolled(object sender, ScrollChangedEventArgs e)
    {
        // The number boxes have scrollers of their own, and theirs bubble up here too.
        if (!ReferenceEquals(e.OriginalSource, SettingsScroll)) return;

        PaintSectionIndex();
    }

    /// <summary>
    /// The row for the section at the top of the view. Where the page does not
    /// scroll, or sits where a click left it, the clicked row is the one.
    /// </summary>
    private void PaintSectionIndex()
    {
        var scroll = SettingsScroll;

        var current = scroll.ScrollableHeight < 1 || Math.Abs(scroll.VerticalOffset - _sectionAt) < 1
            ? _sectionPicked
            : TopSection();

        foreach (var section in _sections) section.Select(ReferenceEquals(section, current));
    }

    /// <summary>The last section whose heading has reached the top of the view.</summary>
    private SettingsSection TopSection()
    {
        // The last heading can't reach the top; at the very end, its section is the one in view.
        if (SettingsScroll.VerticalOffset >= SettingsScroll.ScrollableHeight - 1) return _sections[^1];

        var top = _sections[0];

        foreach (var section in _sections)
        {
            if (HeadingTop(section) <= SettingsScroll.VerticalOffset + 1) top = section;
        }

        return top;
    }

    private double HeadingTop(SettingsSection section) =>
        section.Heading.TransformToAncestor((Visual)SettingsScroll.Content).Transform(default).Y;

    /// <summary>
    /// A number typed into the settings page. Invariant first, then whatever
    /// this machine calls a decimal point — the app writes "88.5" but a keyboard
    /// in a comma locale types "88,5", and refusing that reads as the box being
    /// broken. NaN and infinity are not numbers here.
    /// </summary>
    private static double? Num(string text)
    {
        var ok = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                 || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v);

        return ok && double.IsFinite(v) ? v : null;
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
