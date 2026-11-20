using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CoderMascot.Core;
using CoderMascot.UI;
using Microsoft.Win32;

namespace CoderMascot;

public partial class App : System.Windows.Application, IDashboardHost, IBranchHost, ILogHost
{
    private Mutex? _single;
    private CoderConfig? _cfg;
    private TrayController? _tray;
    private StatusMonitor? _monitor;
    private SessionMonitor? _sessions;
    private SystemMonitor? _load;
    private LeftoverMonitor? _leftovers;
    private RepoMonitor? _repoWatch;
    private CiMonitor? _ciWatch;
    private CiClient? _ci;
    private LogMonitor? _logs;
    private readonly ISecretBox _secrets = new DpapiSecretBox();
    private MainWindow? _dashboard;
    private Reminder? _reminder;
    private NoteBook? _notes;
    private StickyBoard? _stickies;
    private GitReader? _git;

    /// <summary>Last reading per project, so re-opening the window isn't a blank wait.</summary>
    private readonly Dictionary<string, ProjectSnapshot> _repos = new(StringComparer.Ordinal);

    /// <summary>
    /// The background watch's last reading per project, for the nav count only. Kept
    /// apart from <see cref="_repos"/>, which the window treats as recent and won't re-read.
    /// </summary>
    private readonly Dictionary<string, ProjectSnapshot> _watched = new(StringComparer.Ordinal);
    private HotkeyListener? _hotkey;
    private readonly StateHistory _history = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private DispatcherTimer? _watchdog;

    /// <summary>What the mascot is allowed to interrupt with, and what's been dismissed.</summary>
    private readonly AlertGate _alerts = new();

    /// <summary>
    /// Every character currently on screen.
    ///
    /// Each has its own window, bubble and patrol; all of them read from the one
    /// StatusMonitor and the one SessionMonitor below. Adding a character costs
    /// a window, not a poll.
    /// </summary>
    private readonly List<MascotCrewMember> _crew = [];

    /// <summary>Most recent workspace reading, so session news can be merged into it.</summary>
    private WorkspaceSnapshot? _workspace;

    /// <summary>
    /// What the menus need from the display and can't ask it for: the state line,
    /// whether there is anything to dismiss, and whether the session watch is
    /// installed. Set where each reading is painted; the menus read them on open.
    /// </summary>
    private string _menuStatus = "Checking…";
    private bool _dismissable;

    /// <summary>
    /// The session watch needs a workspace-side install, and until that has
    /// happened it reports nothing at all — which is indistinguishable from
    /// reporting that all is well. A watchdog is allowed to be quiet; it is not
    /// allowed to be quiet because nobody plugged it in.
    /// </summary>
    private bool _sessionWatchMissing;

    /// <summary>Deploy branches currently red, from the CI poll. Folded into the state.</summary>
    private IReadOnlyList<BrokenDeploy> _broken = [];

    /// <summary>Each repository's origin URL, resolved once — it rarely changes.</summary>
    private readonly Dictionary<string, string?> _remoteCache = new(StringComparer.Ordinal);

    /// <summary>Something full-screen is running, so we're staying out of the way.</summary>
    private bool _suppressed;

    /// <summary>Distinct failures already shown, so a repeat is logged in silence.</summary>
    private readonly HashSet<string> _reported = [];

    /// <summary>Inside the crash dialog — which pumps messages, so this can re-enter.</summary>
    private bool _reporting;

    private const int CrashDialogLimit = 3;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything else can throw. A watchdog that dies silently is the
        // exact failure it exists to report, so every escape route is covered:
        // the UI thread, the background threads, and tasks nobody awaited.
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            CrashLog.Write("background thread", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            CrashLog.Write("unobserved task", ex.Exception);
            ex.SetObserved();
        };

        // The tray menu is WinForms, which would otherwise show its own error box
        // for a throw in a click or a paint and leave nothing in the log.
        System.Windows.Forms.Application.ThreadException += (_, ex) => CrashLog.Write("tray", ex.Exception);

        // A second instance would double every notification.
        _single = new Mutex(initiallyOwned: true, @"Local\CoderMascot.SingleInstance", out var isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        _cfg = CoderConfig.Load();
        _alerts.Policy = AlertGate.Parse(_cfg.Notifications);
        _notes = NoteBook.Load();

        // Every launch, not just the one where the setting is changed: the exe
        // may have been moved since, which leaves a Run entry pointing at
        // nothing and no symptom other than the mascot quietly not appearing.
        StartupRegistration.Sync(_cfg);

        foreach (var id in _cfg.Characters)
        {
            if (Character.Find(id) is { } character)
                _crew.Add(new MascotCrewMember(_cfg, character));
        }

        // Load() guarantees at least one valid id, but a character whose frames
        // failed to compile in would still leave us with nothing to show.
        if (_crew.Count == 0) _crew.Add(new MascotCrewMember(_cfg, Character.All[0]));

        _git = new GitReader(GitRunners.For(_cfg));
        _stickies = new StickyBoard(_cfg, _notes);
        _stickies.Changed += (_, _) => _dashboard?.RefreshNotes();

        _tray = new TrayController(BuildMenuState);
        _monitor = new StatusMonitor(_cfg);
        // The session watch reaches into the workspace over SSH, so it needs to
        // know when not to bother.
        _sessions = new SessionMonitor(_cfg, () => _monitor?.Latest?.State ?? MascotState.Unknown);
        _load = new SystemMonitor(_cfg);
        _leftovers = new LeftoverMonitor(_cfg);
        _ci = new CiClient((target, tok) => GitRunners.ReadCiToken(_cfg, target.Site, tok));
        _repoWatch = new RepoMonitor(_cfg, ReadForWatch);
        _ciWatch = new CiMonitor(_cfg, ReadCi);
        _logs = new LogMonitor(() => _cfg?.LogSources ?? [], () => _cfg?.LogPollSeconds ?? 5,
            openTable: OpenLogTable, tablePollSeconds: () => (_cfg?.LogTablePollMinutes ?? 5) * 60);
        _reminder = new Reminder(_cfg.RemindAfterSeconds, _cfg.RemindMaxSeconds);

        _sessions.Changed += OnSessionsChanged;
        _load.Changed += OnLoadChanged;
        _leftovers.Changed += (_, _) => Dispatcher.BeginInvoke(Repaint);
        _repoWatch.DeployMoved += OnDeployMoved;
        _ciWatch.BrokenChanged += OnBrokenChanged;
        _ciWatch.Finished += OnPipelinesFinished;
        _monitor.Changed += OnStatusChanged;
        _tray.MenuChosen += (_, entry) => OnMenu(entry, null);

        foreach (var member in _crew) Wire(member);

        // Hot-plugging a monitor or changing resolution invalidates every
        // coordinate every patrol is working from.
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;

        foreach (var member in _crew)
        {
            member.Window.Show();
            member.Bubble.Track(member.Frame, ScreenBounds());

            if (member.Window.HasSprites)
            {
                member.Patrol.Start();
            }
            else
            {
                // Without sprites the window is just an 18px coloured dot; a dot
                // patrolling the screen edges is a mystery, not a mascot.
                Debug.WriteLine($"[CoderMascot] no frames for {member.Character.Id} — patrol disabled");
            }
        }

        // Whatever was on the desktop when the app last closed goes back up,
        // without stealing focus from whatever the user is doing now.
        _stickies.RestoreSaved();
        BindHotkey();

        // Nothing to watch with. Rather than sitting there Unauthorized and
        // pointing at a file the user has to create by hand, ask for the two
        // things it needs — and open the page that hands out the token.
        if (_cfg.NeedsSetup) ShowSetup();

        _monitor.Start();
        _sessions.Start();
        _load.Start();
        _leftovers.Start();
        _repoWatch.Start();
        _ciWatch.Start();
        _logs.Start();

        // Last line of defence. Every other guard tries to keep the poll loop
        // alive; this one makes it visible if the loop ever stops anyway, so a
        // dead monitor can't masquerade as a green, smiling mascot. It also
        // polls for full-screen apps, which has no event to subscribe to.
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watchdog.Tick += OnWatchdogTick;
        _watchdog.Start();
    }

    /// <summary>
    /// An exception that reached the UI thread.
    ///
    /// Kept alive rather than allowed to end the process: nearly everything that
    /// lands here comes from one menu click or one panel, and the poll loop
    /// behind it is still perfectly able to tell you your workspace just died.
    /// Quitting would trade a broken button for a broken watchdog.
    ///
    /// It says so once per incident — silently swallowing exceptions is how an
    /// app ends up lying about its own state.
    /// </summary>
    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write("UI thread", e.Exception);
        e.Handled = true;

        // Everything below is about not making it worse. A throwing timer tick
        // fires 30 times a second, and MessageBox pumps the dispatcher while it
        // is open — so an unguarded dialog here stacks dialogs on top of dialogs
        // until the machine gives up. The log has every incident; the user needs
        // to be told once.
        var signature = $"{e.Exception.GetType().FullName}: {e.Exception.Message}";
        if (_reporting || !_reported.Add(signature)) return;

        if (_reported.Count > CrashDialogLimit)
        {
            _tray?.Notify("Coder Mascot hit another problem", $"Details in {CrashLog.Path_}");
            return;
        }

        _reporting = true;
        try
        {
            MessageBox.Show(
                $"Something in the mascot failed:\n\n{e.Exception.Message}\n\n"
                + $"It has been written to:\n{CrashLog.Path_}\n\n"
                + "The workspace watch is still running.",
                "Coder Mascot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // If even the message box is failing, the log is what's left.
        }
        finally
        {
            _reporting = false;
        }
    }

    /// <summary>Hook up one character's own interactions.</summary>
    private void Wire(MascotCrewMember member)
    {
        member.Patrol.Moved += (_, rect) => member.Bubble.FollowMascot(rect, ScreenBounds());

        // Parked while that character's menu is open, so the window doesn't walk
        // out from under it.
        member.Window.PatrolToggled += (_, on) =>
            member.Patrol.SetEnabled(on && member.Wanted && !_suppressed);

        // Hovering or dragging parks that mascot so it can actually be clicked.
        member.Window.HoldChanged += (_, held) =>
        {
            if (held) member.Patrol.BeginHold();
            else member.Patrol.EndHold();
        };

        // Poking any one of them speaks for the whole crew — they are all
        // reporting the same workspace, so dismissing three times over would
        // just be the multi-character version of the thing being complained
        // about.
        member.Window.Acknowledged += (_, _) => Acknowledge();

        member.Window.MenuSource = BuildMenuState;
        member.Window.MenuChosen += (_, entry) => OnMenu(entry, member.Window);
    }

    // ---------- how much it's allowed to interrupt ----------

    /// <summary>
    /// "I know" — stop pressing this particular point.
    ///
    /// Only the interrupting stops. The badge stays whatever colour the truth
    /// is, and the tray tooltip still says what's wrong: a dismissed alarm is a
    /// quiet alarm, never a cleared one.
    /// </summary>
    private void Acknowledge()
    {
        if (!_presented.NeedsAttention()) return;

        _alerts.Acknowledge(_presented);

        // Repaint rather than just hiding the bubbles: the mascot also has to be
        // released from its alert corner and sent back to patrolling, and that
        // decision lives in one place.
        Repaint();
    }

    private void SetAlertPolicy(AlertPolicy policy)
    {
        if (_cfg is null) return;

        _alerts.Policy = policy;
        _cfg.Notifications = AlertGate.Text(policy);
        _cfg.Save();

        Repaint();
    }

    /// <summary>
    /// Re-run the current reading through the display, without treating it as
    /// news. For changes that came from the user rather than from a poll.
    /// </summary>
    private void Repaint()
    {
        var ws = _workspace ?? _monitor?.Latest;
        if (ws is not null) Present(Combine(ws, _sessions?.Latest, _load?.Latest), notify: false);
    }

    // ---------- the menus ----------

    /// <summary>
    /// Everything the mascot's menu and the tray's are built from, read fresh each
    /// time either one opens. Nothing is pushed into a menu, so they can't
    /// disagree.
    /// </summary>
    private MenuState BuildMenuState() => new(
        _menuStatus,
        _sessionWatchMissing,
        _dismissable,
        _alerts.Policy,
        // Wanted, not Visibility: a full-screen app hides the crew without anyone asking for that.
        [.. _crew.Select(m => new MenuCharacter(m.Character.Id, m.Character.Display, m.Wanted))],
        _cfg?.Patrol ?? false,
        _crew.Count > 0 && _crew[0].Window.IsClickThrough,
        _cfg is { StartWithWindows: true } && StartupRegistration.IsEnabled(),
        DiagramMenu.Build((_cfg?.Projects ?? []).Select(p => p.ToProject())));

    /// <summary>
    /// A line was chosen in either menu. <paramref name="origin"/> is the
    /// character whose own menu it was, or null for the tray.
    /// </summary>
    private void OnMenu(MenuEntry entry, MascotWindow? origin)
    {
        if (_cfg is null) return;

        switch (entry.Command)
        {
            case MenuCommand.SessionWatchHelp:
                ShowSessionWatchHelp();
                break;

            case MenuCommand.Dismiss:
                Acknowledge();
                break;

            case MenuCommand.Alerts:
                if (Enum.TryParse(entry.Argument, out AlertPolicy policy) && Enum.IsDefined(policy))
                {
                    SetAlertPolicy(policy);

                    // The Settings page shows the same choice.
                    _dashboard?.RefreshSettings();
                }
                break;

            case MenuCommand.OpenMain:
                ShowDashboard();
                break;

            case MenuCommand.Projects:
                ShowBranches();
                break;

            case MenuCommand.Diagram:
                if (entry.Argument is { } target) OpenDiagram(target, origin);
                break;

            case MenuCommand.NewNote:
                NewSticky();
                break;

            case MenuCommand.CheckNow:
                _monitor?.PollNow();
                break;

            case MenuCommand.OpenCoder:
                OpenCoderFromMenu(_cfg);
                break;

            case MenuCommand.Connect:
                ShowSetup();
                break;

            case MenuCommand.EditConfig:
                EditConfig(_cfg);
                break;

            case MenuCommand.ToggleMascot:
                ToggleMascot(entry);
                break;

            case MenuCommand.Patrol:
                TogglePatrol(entry);
                break;

            case MenuCommand.ClickThrough:
                ToggleClickThrough(entry, origin);
                break;

            case MenuCommand.StartWithWindows:
                ToggleStartup(entry);
                break;

            case MenuCommand.Quit:
                // Before Shutdown(), which closes the window itself and would
                // ask about unapplied settings on the way out.
                _dashboard?.CloseForShutdown();
                Shutdown();
                break;
        }
    }

    private static void ShowSessionWatchHelp()
    {
        MessageBox.Show(
            "The mascot can also tell you when a Claude session is waiting for "
            + "your confirmation, or has stopped making progress.\n\n"
            + "That part runs inside the workspace and isn't installed yet. "
            + "In a terminal in your Coder workspace:\n\n"
            + "    bash ~/workspace/projects/coder-mascot/workspace/install-hooks.sh\n\n"
            + "Then restart any Claude sessions already running, so they pick up "
            + "the hooks.",
            "Coder Mascot — session watch", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void OpenCoderFromMenu(CoderConfig cfg)
    {
        if (CoderUri(cfg) is { } uri) Shell(uri.AbsoluteUri);
    }

    /// <summary>
    /// The Coder address, only if it parses as http(s). UseShellExecute means "ask
    /// the shell what this string means", not "open a URL" — a config value of
    /// \\host\share\payload.exe would be launched. Every shell-open of it comes here.
    /// </summary>
    private static Uri? CoderUri(CoderConfig? cfg) =>
        Uri.TryCreate(cfg?.Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    private static void EditConfig(CoderConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(CoderConfig.Dir);
            if (!File.Exists(CoderConfig.Path_)) cfg.Save();
            Shell(CoderConfig.Path_);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open config: {ex.Message}",
                "Coder Mascot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void Shell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {target}: {ex.Message}",
                "Coder Mascot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// A reply to a click, so a failure is said whatever the noise level: the
    /// user is looking straight at the mascot (or the tray), waiting for a
    /// diagram to appear.
    /// </summary>
    private async void OpenDiagram(string target, MascotWindow? origin)
    {
        var problem = await Task.Run(() => DesktopActions.OpenDiagram(target));
        if (problem is null) return;

        if (origin is null)
        {
            _tray?.Notify("Couldn't open that diagram", problem);
            return;
        }

        var member = _crew.FirstOrDefault(m => ReferenceEquals(m.Window, origin));

        // The character may have been hidden or sent away while the disk
        // answered, and a closed bubble throws if asked to speak.
        if (member is null || !member.Wanted) return;

        member.Bubble.Track(member.Frame, ScreenBounds());
        member.Bubble.Say("Couldn't open that diagram", problem, sticky: false);
    }

    private void ToggleMascot(MenuEntry entry)
    {
        var member = _crew.FirstOrDefault(m => m.Character.Id == entry.Argument);
        if (member is null || _cfg is null) return;

        var on = entry.Checked != true;
        member.Wanted = on;

        var show = on && !_suppressed;
        member.Window.Visibility = show ? Visibility.Visible : Visibility.Hidden;

        // AllowsTransparency forces software rendering, so looping
        // storyboards behind a hidden window burn CPU for nothing.
        member.Window.SetAnimating(show);
        member.Patrol.SetEnabled(show && _cfg.Patrol);
        if (!on) member.Bubble.Dismiss();
    }

    private void TogglePatrol(MenuEntry entry)
    {
        if (_cfg is null) return;

        _cfg.Patrol = entry.Checked != true;
        _cfg.Save();

        foreach (var member in _crew)
            member.Patrol.SetEnabled(_cfg.Patrol && member.Wanted && !_suppressed);
    }

    private void ToggleClickThrough(MenuEntry entry, MascotWindow? origin)
    {
        var on = entry.Checked != true;
        foreach (var member in _crew) member.Window.SetClickThrough(on);

        // Once clicks pass through, the only way back is the tray menu.
        if (on && origin is not null)
        {
            MessageBox.Show(
                _crew.Count > 1
                    ? "The mascots now ignore the mouse. Turn this off from the tray icon menu."
                    : "The mascot now ignores the mouse. Turn this off from the tray icon menu.",
                "Coder Mascot", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ToggleStartup(MenuEntry entry)
    {
        var enable = entry.Checked != true;

        if (ApplyStartWithWindows(enable) is { } problem)
        {
            MessageBox.Show($"Could not change startup setting: {problem}",
                "Coder Mascot", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The Settings page shows the same switch.
        _dashboard?.RefreshSettings();
    }

    // ---------- the dashboard ----------

    /// <summary>
    /// Open the detail view, creating it the first time.
    ///
    /// Kept alive once opened rather than rebuilt: it holds the settings fields
    /// and the scroll position, and closing it only hides it.
    /// </summary>
    private void ShowDashboard(bool focusNotes = false) => Dashboard().Summon(focusNotes);

    private MainWindow Dashboard()
    {
        if (_dashboard is null)
        {
            _dashboard = new MainWindow(this);

            // A closed WPF window cannot be shown again, so forget it and build
            // a fresh one next time rather than throwing on the second open.
            _dashboard.Closed += (_, _) => _dashboard = null;
        }

        return _dashboard;
    }

    /// <summary>
    /// Ask for a Coder address and a token, in the main window's drawer.
    ///
    /// Never more than one of these: opened from a menu while the first-run copy
    /// is already up, a second form would let two of them save different
    /// credentials over each other. The drawer holds one form at a time.
    /// </summary>
    private void ShowSetup()
    {
        if (_cfg is null) return;

        Dashboard().ShowConnect();
    }

    /// <summary>
    /// Where work has got to, across every repository: the Projects page of the
    /// main window, which this opens (and creates, if it is not up).
    /// </summary>
    private void ShowBranches() => Dashboard().ShowProjects();

    IReadOnlyList<GitProject> IBranchHost.Projects =>
        [.. (_cfg?.Projects ?? []).Select(p => p.ToProject())];

    ProjectSnapshot? IBranchHost.Cached(GitProject project) =>
        _repos.TryGetValue(project.Id, out var snapshot) ? snapshot : null;

    IReadOnlyList<ProjectEntry> IBranchHost.Configured => _cfg?.Projects ?? [];

    IReadOnlyList<GitProject> IBranchHost.SaveProjects(IReadOnlyList<ProjectEntry> entries)
    {
        if (_cfg is null) return [];

        // Through the same sieve the file goes through, so what the editor gets
        // back is what a restart would have produced — no blank paths, no two
        // rows for one folder.
        _cfg.SetProjects(entries);
        _cfg.Save();

        // A reading of a repository nobody is watching any more is dead weight,
        // and a long session spent correcting a path accumulates one per attempt.
        var live = _cfg.Projects.Select(p => p.ToProject().Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _repos.Keys.Where(id => !live.Contains(id)).ToList()) _repos.Remove(gone);
        foreach (var gone in _watched.Keys.Where(id => !live.Contains(id)).ToList()) _watched.Remove(gone);

        return [.. _cfg.Projects.Select(p => p.ToProject())];
    }

    IReadOnlyList<string> IBranchHost.Collapsed => _cfg?.CollapsedGroups ?? [];

    void IBranchHost.SetCollapsed(IEnumerable<string> keys)
    {
        if (_cfg is null) return;

        _cfg.SetCollapsed(keys);
        _cfg.Save();
    }

    async Task<ProbeReport> IBranchHost.ProbeAsync(GitProject project, bool fetch, CancellationToken ct)
    {
        if (_git is null || _cfg is null) return ProbeReport.Failed("Not ready yet.");

        // A URL-only repository has nothing to look at until it has been
        // downloaded once, so a failed fetch here is a failed check — unlike the
        // Projects page, where a fetch failure still leaves a real reading on
        // disk to fall back to.
        string? note = null;

        if (fetch)
        {
            var pulled = await Fetch(project, ct);
            if (!pulled.Ok) return ProbeReport.Failed(pulled.Problem!);
            note = pulled.Warning;
        }

        var report = await _git.ProbeAsync(project, ct);
        return note is null ? report : report with { Warning = note };
    }

    async Task<IReadOnlyList<string>> IBranchHost.DiscoverAsync(RepoHost host, CancellationToken ct) =>
        _cfg is null ? [] : await GitRunners.FindRepositories(_cfg, host, ct);

    async Task<RemoteTest> IBranchHost.TestAsync(GitProject project, CancellationToken ct) =>
        _cfg is null
            ? RemoteTest.Failed("Not ready yet.", null)
            : await GitRunners.TestRemote(_cfg, project.Path, ct);

    async Task<string?> IBranchHost.SignInAsync(
        GitProject project, string username, string secret, CancellationToken ct) =>
        _cfg is null
            ? "Not ready yet."
            : await GitRunners.StoreCredential(_cfg, project.Path, username, secret, ct);

    async Task<ProjectSnapshot> IBranchHost.ReadAsync(GitProject project, bool fetch, CancellationToken ct)
    {
        if (_git is null || _cfg is null)
            return new ProjectSnapshot { Name = project.Name, Path = project.Path, Problem = "Not ready yet." };

        string? note = null;

        if (fetch)
        {
            // A fetch that couldn't reach the server does not invalidate what is
            // already on disk. Reporting it as a failure of the *read* would
            // blank a perfectly good branch list — on the machine most likely to
            // have no credentials for the remote, which is exactly where the
            // list is the only thing you have.
            var pulled = await Fetch(project, ct);

            note = pulled.Ok
                ? pulled.Warning
                : $"Fetch failed — {pulled.Problem!.TrimEnd('.')}. Showing what is on disk.";
        }

        var snapshot = await _git.ReadAsync(project, ct);
        if (note is not null)
            snapshot = snapshot with { Warning = snapshot.Warning ?? note };

        return Remember(project, snapshot, ct);
    }

    /// <summary>
    /// Bring a repository up to date from its server. Null when it worked.
    ///
    /// This talks to the network, can ask for credentials, and on the first fetch
    /// of a URL-only repository is minutes of real work — so it never runs on a
    /// window opening. Its callers are the user's own actions and the background
    /// auto-fetch (<see cref="RepoMonitor"/>, on its slow timer), never a render
    /// path. Those two can race on one repo; git's own lock makes one fail, which
    /// surfaces as a "Fetch failed" note rather than anything corrupt.
    /// </summary>
    private async Task<FetchOutcome> Fetch(GitProject project, CancellationToken ct)
    {
        if (_cfg is null) return FetchOutcome.Failed("Not ready yet.");

        // A URL-only repository may not have anywhere to fetch *into* yet.
        // Creating it is local and instant; filling it is the fetch below.
        if (project.Host == RepoHost.Remote)
        {
            var ready = await GitRunners.PrepareMirror(_cfg, project, ct);
            if (!ready.Ok) return FetchOutcome.Failed(Trim(ready.Error));
        }

        var pull = await GitRunners.For(_cfg)(project, ["fetch", "--all", "--prune"], ct);

        // Written only on a path that proves a fetch completed. git's own traces
        // don't: init creates the object store before any fetch, and a fetch that
        // dies asking for a password still writes FETCH_HEAD. Believing either
        // turns a refused sign-in into a repository reporting, in green, that it
        // has no branches.
        void Mark()
        {
            if (project.Host == RepoHost.Remote && GitUrl.Clean(project.Path) is { } url)
                MirrorStore.MarkFetched(url);
        }

        // The other direction, and just as necessary: a copy we now know is
        // unusable must stop claiming it was downloaded, or the claim outlives
        // the attempt that made it and there is no way back to Download.
        void Unmark()
        {
            if (project.Host == RepoHost.Remote && GitUrl.Clean(project.Path) is { } url)
                MirrorStore.ClearFetched(url);
        }

        if (pull.Ok)
        {
            Mark();
            return FetchOutcome.Done;
        }

        // Non-zero, but everything git objected to was a branch whose *name* this
        // filesystem won't take. The objects and the other branches are here and
        // are current, so this counts as a download — with a footnote naming what
        // is missing, because a branch that silently never appears is worse than
        // one explained.
        var unstorable = GitRefs.Unstorable(pull.Error);

        if (GitRefs.OnlyUnstorable(pull.Error) && GitRefs.Explain(unstorable) is { } missing)
        {
            // Everything git objected to was a branch whose name this filesystem
            // won't take. Git can be told to leave those out, so ask again
            // without them rather than settling for what the first attempt
            // happened to leave behind — the first attempt is not reliably
            // partial, and a copy holding some unknown fraction of the
            // repository is not something to report as a download.
            if (GitRefs.SkipArgs(unstorable) is { } around)
            {
                var again = await GitRunners.For(_cfg)(project, around, ct);
                if (again.Ok)
                {
                    Mark();
                    return FetchOutcome.Partial(missing);
                }
            }

            // Couldn't be worked around — an older git without negative
            // refspecs, or something else wrong as well. Do not claim a
            // download: a copy marked fetched with nothing in it reports "no
            // branches in it" as though that were the answer, and hides the
            // button that would fix it.
            Unmark();

            return FetchOutcome.Failed("Nothing could be downloaded. " + missing);
        }

        // The one failure with an answer on screen. Git's own sentence for it
        // ("could not read Username for …: terminal prompts disabled") sends
        // people looking at their network, their URL and their repository —
        // anywhere but the two boxes that fix it.
        return FetchOutcome.Failed(GitSignIn.Needed(pull.Error) ? GitSignIn.Ask : Trim(pull.Error));
    }

    /// <summary>
    /// Keep a reading for next time — unless it was abandoned.
    ///
    /// A cancelled read produces a snapshot that looks like an answer and isn't:
    /// no branches, or a merged set that never arrived. Caching it means the
    /// window shows that on the way back and then declines to re-read it,
    /// because it counts as recent.
    /// </summary>
    private ProjectSnapshot Remember(GitProject project, ProjectSnapshot snapshot, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested) _repos[project.Id] = snapshot;
        return snapshot;
    }

    private static string Trim(string text)
    {
        var clean = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return clean.Length > 160 ? clean[..160] + "…" : clean;
    }

    /// <summary>A new note on the desktop, under the pointer, ready to type into.</summary>
    private void NewSticky() => _stickies?.CreateAtCursor();

    /// <summary>
    /// Bind the global shortcut, and say so if it could not be.
    ///
    /// A shortcut that silently does nothing is worse than no shortcut: the user
    /// presses it, gets no note, and concludes the feature is broken rather than
    /// that something else on their machine owns those keys.
    /// </summary>
    private void BindHotkey()
    {
        if (_cfg is null) return;
        if (string.IsNullOrWhiteSpace(_cfg.NoteHotkey)) return;

        _hotkey = new HotkeyListener();
        _hotkey.Pressed += (_, _) => NewSticky();

        if (_hotkey.Register(_cfg.NoteHotkey) is not { } problem) return;

        CrashLog.Write("hotkey", new InvalidOperationException(problem));
        _tray?.Notify("Note shortcut unavailable",
            $"{problem} The tray menu still makes notes.");
    }

    CoderConfig IDashboardHost.Config => _cfg ??= CoderConfig.Load();
    WorkspaceSnapshot? IDashboardHost.Workspace => _workspace ?? _monitor?.Latest;
    SessionSnapshot? IDashboardHost.Sessions => _sessions?.Latest;
    LoadSnapshot IDashboardHost.Load => _load?.Latest ?? LoadSnapshot.Idle;
    IReadOnlyList<DevServer> IDashboardHost.Leftovers => _leftovers?.Stale ?? [];
    TimeSpan? IDashboardHost.WorkspaceIdle => CurrentLeftovers(_workspace ?? _monitor?.Latest).WorkspaceIdle;
    StateHistory IDashboardHost.History => _history;
    NoteBook IDashboardHost.Notes => _notes ??= NoteBook.Load();

    void IDashboardHost.StickToDesktop(string noteId, bool stick)
    {
        if (stick) _stickies?.Open(noteId);
        else _stickies?.Close(noteId);
    }

    void IDashboardHost.NoteChanged(string noteId) => _stickies?.Refresh(noteId);

    string? IDashboardHost.NoteHotkey => _hotkey?.Bound;

    /// <summary>Last port sweep, and when it was taken.</summary>
    private IReadOnlyList<Listener> _portCache = [];
    private DateTime _portsAt = DateTime.MinValue;
    private Task<IReadOnlyList<Listener>>? _portScan;

    /// <summary>
    /// Sweep the TCP table, off the UI thread and at most every few seconds.
    ///
    /// Both halves matter. The sweep walks every listening socket and opens a
    /// handle per owning process, which is far too much to do on the thread that
    /// is drawing the window; and the dashboard repaints once a second, which is
    /// far more often than the set of listening ports actually changes.
    ///
    /// Callers share one in-flight scan rather than starting their own — the
    /// tick and a button press arriving together should cost one sweep.
    /// </summary>
    async Task<IReadOnlyList<Listener>> IDashboardHost.PortsAsync(bool fresh)
    {
        if (!fresh && (DateTime.UtcNow - _portsAt).TotalSeconds < 4) return _portCache;

        var scan = _portScan ??= Task.Run(() => PortScanner.ScanAll());
        try
        {
            _portCache = await scan.ConfigureAwait(true);
            _portsAt = DateTime.UtcNow;
        }
        finally
        {
            if (ReferenceEquals(_portScan, scan)) _portScan = null;
        }

        return _portCache;
    }

    void IDashboardHost.CheckNow() => _monitor?.PollNow();

    void IDashboardHost.OpenCoder()
    {
        if (CoderUri(_cfg) is not { } uri) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CoderMascot] could not open Coder: {ex.Message}");
        }
    }

    async Task<string?> IDashboardHost.TransitionAsync(string transition)
    {
        var id = (_workspace ?? _monitor?.Latest)?.WorkspaceId;
        if (string.IsNullOrWhiteSpace(id)) return "Don't know which workspace this is yet.";
        if (_monitor is null) return "Not connected.";

        return await _monitor.TransitionAsync(id!, transition);
    }

    void IDashboardHost.RescanLeftovers() => _leftovers?.ScanNow();

    void IDashboardHost.Reconnected()
    {
        // The token is read per request, so the new one is already in use —
        // but the workspace the old one resolved to is not necessarily this
        // account's, and the poll loop is otherwise up to 20 seconds away
        // from noticing any of it.
        _monitor?.Reauthenticate();
        _sessions?.CheckForStall();
        _dashboard?.RefreshSettings();
    }

    IBranchHost IDashboardHost.Branches => this;

    ILogHost IDashboardHost.Logs => this;

    IReadOnlyList<LogSourceEntry> ILogHost.Sources => _cfg?.LogSources ?? [];

    LogMonitor ILogHost.Reader => _logs ?? throw new InvalidOperationException("The log reader starts with the app.");

    IReadOnlyList<LogSourceEntry>? ILogHost.SaveSources(IReadOnlyList<LogSourceEntry> entries)
    {
        if (_cfg is null) return null;

        _cfg.SetLogSources(entries);
        var saved = _cfg.Save();
        _logs?.ReadNow();
        return saved ? _cfg.LogSources : null;
    }

    string ILogHost.Seal(string connection) => _secrets.Seal(connection);

    string? ILogHost.HostOf(LogSourceEntry draft, string connection) =>
        draft.Kind == LogSourceKinds.Mongo ? MongoLogReader.HostOf(connection, draft.Database) : SqlLogReader.HostOf(connection);

    async Task<TableProbe> ILogHost.ProbeAsync(LogSourceEntry draft, string? connection, CancellationToken ct)
    {
        var plain = connection ?? (draft.Connection is { } saved ? _secrets.Open(saved) : null);
        if (string.IsNullOrWhiteSpace(plain)) return new TableProbe("Paste the connection string.", null, [], [], []);

        return draft.Kind == LogSourceKinds.Mongo
            ? await MongoLogReader.ProbeAsync(plain, draft, ct).ConfigureAwait(false)
            : await SqlLogReader.ProbeAsync(plain, draft, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A reader for a table source, its connection string opened only here, in memory.
    /// One left in plain text by a hand edit is refused rather than used: using it would
    /// make the plain copy look fine.
    /// </summary>
    private ILogTableReader OpenLogTable(LogSourceEntry source)
    {
        if (!LogSecrets.IsSealed(source.Connection))
            throw new LogTableException("This connection string is stored as plain text. Open Edit and paste it again so it can be encrypted.");

        var plain = _secrets.Open(source.Connection!)
                    ?? throw new LogTableException("This connection string was saved by another Windows account or on another PC. Open Edit and paste it again.");

        return source.Kind == LogSourceKinds.Mongo ? new MongoLogReader(plain, source) : new SqlLogReader(plain, source);
    }

    int IDashboardHost.OutstandingBranches
    {
        get
        {
            var newest = new Dictionary<string, ProjectSnapshot>(_watched, StringComparer.Ordinal);
            foreach (var (id, snap) in _repos)
            {
                if (!newest.TryGetValue(id, out var other) || other.ReadAt < snap.ReadAt) newest[id] = snap;
            }

            return newest.Values.Sum(s => s.Ok ? s.Outstanding : 0);
        }
    }

    void IDashboardHost.SetAlertPolicy(AlertPolicy policy) => SetAlertPolicy(policy);

    string? IDashboardHost.SetStartWithWindows(bool on) => ApplyStartWithWindows(on);

    private string? ApplyStartWithWindows(bool on)
    {
        if (_cfg is null) return "Not ready yet.";

        var error = StartupRegistration.Set(on);
        if (error is not null) return error;

        _cfg.StartWithWindows = on;
        _cfg.Save();
        return null;
    }

    void IDashboardHost.SettingsChanged()
    {
        // Thresholds live inside the load watch's state machine, so it has to be
        // rebuilt; the leftover numbers are read per scan and need nothing.
        _load?.Reconfigure();
        _leftovers?.ScanNow();
        Repaint();
    }

    private int _stallTicks;

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        // The stall check is a 15s concern; don't run it every 2s.
        if (++_stallTicks >= 8)
        {
            _stallTicks = 0;
            _monitor?.CheckForStall();
            _sessions?.CheckForStall();
        }

        NudgeIfForgotten();

        if (_cfg?.HideWhenFullScreen != true) return;

        var busy = IsFullScreenAppRunning();
        if (busy == _suppressed) return;

        _suppressed = busy;

        // A topmost window walking across a game, a video, or — worst — a shared
        // presentation is not charming.
        foreach (var member in _crew)
        {
            var show = !busy && member.Wanted;
            member.Window.Visibility = show ? Visibility.Visible : Visibility.Hidden;
            member.Window.SetAnimating(show);
            member.Patrol.SetEnabled(show && _cfg.Patrol);
            if (busy) member.Bubble.Dismiss();
        }

        // Notes are topmost too, and a note lying across a shared presentation
        // is exactly the surprise the mascot is already hidden to avoid.
        _stickies?.SetVisible(!busy);
    }

    /// <summary>
    /// Bring an unanswered thing back up.
    ///
    /// One notification at the moment something happens is the wrong shape for
    /// forgetting: the prompt appeared while you were in a full-screen editor,
    /// or on the other monitor, and the toast is long gone by the time you look
    /// back. So the mascot says it again, less and less often.
    ///
    /// Nothing fires while a full-screen app is up — the shell is already
    /// suppressing notifications there, and a mascot that overrides that during
    /// a screen share is worse than a missed prompt.
    /// </summary>
    private void NudgeIfForgotten()
    {
        if (_reminder is null || _cfg is null || _cfg.RemindAfterSeconds <= 0) return;
        if (_suppressed || !_alerts.AllowReminder(_presented)) return;

        var now = _clock.Elapsed.TotalSeconds;
        if (!_reminder.Due(now)) return;

        var text = _reminder.Text(_presented.Title(), now);
        _tray?.Notify(text, _detail);

        // Say it above the mascot too. The tray balloon is easy to miss twice
        // for exactly the same reason it was missed the first time.
        foreach (var member in _crew)
        {
            if (member.Wanted) member.Bubble.Say(text, _detail, sticky: true);
        }
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var member in _crew) member.Patrol.OnDisplaysChanged();
        });

    private static Rect ScreenBounds() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    /// <summary>
    /// Merge the two independent signals.
    ///
    /// The workspace being unreachable outranks anything happening inside it —
    /// if the agent is gone, "Claude needs you" is both untrue and unactionable.
    /// Only once the workspace is healthy do the session states get a say.
    /// </summary>
    private WorkspaceSnapshot Combine(WorkspaceSnapshot ws, SessionSnapshot? s, LoadSnapshot? load)
    {
        var merged = CombineSessions(ws, s);

        // Only ever displace good news. Everything below is housekeeping: real
        // enough to mention, never important enough to hide a dropped agent or
        // a session waiting on you.
        if (merged.State is not (MascotState.Connected or MascotState.Unknown))
            return merged;

        // A red deploy branch is discrete and actionable, so it outranks the
        // ambient housekeeping below — high load and leftovers are always there.
        if (_broken.Count > 0)
            return merged with { State = MascotState.PipelineFailed, Detail = CiWatch.Describe(_broken) };

        // The machine's own load comes first of the two, because it is costing
        // you speed *now* — a leftover is only costing you tidiness.
        if (load is { Loaded: true })
            return merged with { State = MascotState.ResourcesHigh, Detail = load.Detail };

        var leftovers = CurrentLeftovers(ws);
        if (leftovers.Any)
            return merged with { State = MascotState.Leftovers, Detail = leftovers.Detail };

        return merged;
    }

    /// <summary>What is running that probably shouldn't be, right now.</summary>
    private LeftoverReport CurrentLeftovers(WorkspaceSnapshot? ws)
    {
        if (_cfg is not { WatchLeftovers: true }) return new LeftoverReport();

        // Only call a *running* workspace idle. A stopped one is not something
        // left on, and offering to stop it again would be nonsense.
        var idle = ws?.BuildStatus is "running"
            ? LeftoverWatch.WorkspaceIdleFor(ws?.LastUsedAt, DateTimeOffset.UtcNow, _cfg.WorkspaceIdleMinutes)
            : null;

        return LeftoverWatch.Build(idle, _leftovers?.Stale ?? []);
    }

    private static WorkspaceSnapshot CombineSessions(WorkspaceSnapshot ws, SessionSnapshot? s)
    {
        if (s is null || !s.Ok || !s.Installed) return ws;

        // AutoStopSoon outranks session news too: auto-stop kills every session
        // including the one that's waiting, and unlike the others it has a
        // deadline the user can still act on. Starting is excluded because a
        // booting workspace can't have live sessions, so the reading is stale.
        if (ws.State.IsAlarm() ||
            ws.State is MascotState.Unknown or MascotState.AutoStopSoon or MascotState.Starting)
            return ws;

        if (s.Waiting > 0)
            return ws with { State = MascotState.NeedsConfirmation, Detail = s.Detail };

        if (s.Stalled > 0)
            return ws with { State = MascotState.SessionStalled, Detail = s.Detail };

        return ws;
    }

    private void OnSessionsChanged(object? sender, SessionSnapshot s) =>
        Dispatcher.BeginInvoke(() =>
        {
            // A watch that was never installed reports nothing, which on screen
            // is identical to a watch reporting that nothing is wrong. Say so.
            _sessionWatchMissing = s.Ok && !s.Installed;

            // Re-render the last workspace reading through the new session news,
            // rather than waiting for the next workspace poll.
            var ws = _workspace ?? _monitor?.Latest;
            if (ws is not null) Present(Combine(ws, s, _load?.Latest), notify: true);

            AnnounceFinished(s);
        });

    /// <summary>
    /// "Claude finished."
    ///
    /// Kept apart from the state machine on purpose. Finishing is an event, not
    /// a condition — there is no such thing as *being* in the finished state,
    /// and modelling it as one would leave the mascot sitting on "done" until
    /// something else happened. So it says it once and the badge stays green.
    /// </summary>
    private void AnnounceFinished(SessionSnapshot s)
    {
        if (_cfg is not { AnnounceFinished: true } || s.JustFinished.Count == 0) return;

        var text = SessionDiff.Describe(s.JustFinished);
        _history.Note(text);

        if (_suppressed || !_alerts.AllowToast(MascotState.Connected)) return;

        _tray?.Notify("Claude finished", text);

        foreach (var member in _crew)
        {
            if (member.Wanted) member.Bubble.Say("Claude finished", text, sticky: false);
        }
    }

    // ---------- watched-repository pipelines ----------

    private void OnBrokenChanged(object? sender, IReadOnlyList<BrokenDeploy> broken) =>
        Dispatcher.BeginInvoke(() =>
        {
            // Repaint folds the red deploys into the state, colour and detail line.
            _broken = broken;
            Repaint();
        });

    private void OnPipelinesFinished(object? sender, IReadOnlyList<PipelineNews> news) =>
        Dispatcher.BeginInvoke(() => AnnouncePipelines(news));

    private void OnDeployMoved(object? sender, string line) =>
        Dispatcher.BeginInvoke(() => AnnounceEvent("Deploy branch moved", line));

    /// <summary>"Pipeline failed", or "N pipelines finished" — one-shot, like a session finishing.</summary>
    private void AnnouncePipelines(IReadOnlyList<PipelineNews> news)
    {
        if (news.Count == 0) return;

        var (title, text) = news.Count == 1
            ? CiWatch.Describe(news[0].Project, news[0].Pipeline)
            : CiWatch.Describe(news);

        AnnounceEvent(title, text);
    }

    /// <summary>A one-shot toast and bubble, the same shape as "Claude finished".</summary>
    private void AnnounceEvent(string title, string text)
    {
        _history.Note(text);

        if (_suppressed || !_alerts.AllowToast(MascotState.Connected)) return;

        _tray?.Notify(title, text);

        foreach (var member in _crew)
        {
            if (member.Wanted) member.Bubble.Say(title, text, sticky: false);
        }
    }

    /// <summary>
    /// Fetch then read a repository for the background watch, without touching the
    /// window's own cache. Runs on the monitor's loop thread, so it stays off the
    /// dictionary the UI thread reads.
    /// </summary>
    private async Task<ProjectSnapshot> ReadForWatch(GitProject project, CancellationToken ct)
    {
        if (_git is null || _cfg is null)
            return new ProjectSnapshot { Name = project.Name, Path = project.Path, Problem = "Not ready yet." };

        // A fetch that couldn't reach the server does not invalidate what is on
        // disk; read it either way, exactly as the window does.
        try { await Fetch(project, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* read on-disk regardless */ }

        var snapshot = await _git.ReadAsync(project, ct);

        // The sweep may still hold a project the editor has just removed.
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_cfg?.Projects.Any(p => p.ToProject().Id == project.Id) == true) _watched[project.Id] = snapshot;
        });

        return snapshot;
    }

    /// <summary>Resolve a repository's CI and read it, or null when it has none.</summary>
    private async Task<CiSnapshot?> ReadCi(GitProject project, CancellationToken ct)
    {
        if (_ci is null || _cfg is null) return null;

        string? remote = null;
        if (project.Host != RepoHost.Remote && !_remoteCache.TryGetValue(project.Id, out remote))
        {
            remote = await RemoteUrl(project, ct);

            // Cache only a real answer. RemoteUrl returns null on any git failure,
            // including a workspace that was briefly unreachable over coder ssh;
            // pinning that would leave CI dead for the repo until restart.
            if (remote is not null) _remoteCache[project.Id] = remote;
        }

        return CiRemote.Resolve(project, remote) is { } target
            ? await _ci.ReadAsync(project, target, ct)
            : null;
    }

    private async Task<string?> RemoteUrl(GitProject project, CancellationToken ct)
    {
        if (_cfg is null) return null;
        try
        {
            var seen = await GitRunners.For(_cfg)(project, ["remote", "get-url", "origin"], ct);
            return seen.Ok ? seen.Text.Trim() : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private void OnLoadChanged(object? sender, LoadSnapshot load) =>
        Dispatcher.BeginInvoke(() =>
        {
            var ws = _workspace ?? _monitor?.Latest;
            if (ws is not null) Present(Combine(ws, _sessions?.Latest, load), notify: true);
        });

    private void OnStatusChanged(object? sender, StateChangedEventArgs e)
    {
        // The poll loop is a background task; all painting must hop to the UI thread.
        Dispatcher.BeginInvoke(() =>
        {
            _workspace = e.Snapshot;
            Present(Combine(e.Snapshot, _sessions?.Latest, _load?.Latest), e.ShouldNotify);
        });
    }

    /// <summary>Last state actually shown, so the same reading never re-toasts.</summary>
    private MascotState _presented = (MascotState)(-1);

    /// <summary>The line that went with `_presented`, for repeat notifications.</summary>
    private string _detail = string.Empty;

    /// <summary>Paint one combined reading everywhere.</summary>
    private void Present(WorkspaceSnapshot snap, bool notify)
    {
        // The session monitor polls every 30s. Without this, a steady
        // "Claude needs you" would fire a Windows toast twice a minute, forever.
        var changed = snap.State != _presented;
        notify = notify && changed;
        _presented = snap.State;
        _detail = snap.Detail;

        // A different problem is a different problem: whatever was dismissed
        // stops applying, and this one gets its own full chance to interrupt.
        _alerts.Observe(snap.State);

        if (changed) _history.Record(snap.State, snap.Detail);

        // Restart the nudge clock when the subject changes, so a fresh problem
        // gets the full quiet period rather than inheriting the previous one's
        // stretched-out backoff.
        if (snap.State.NeedsAttention())
        {
            if (changed) _reminder?.Clear();
            _reminder?.Begin(_clock.Elapsed.TotalSeconds);
        }
        else
        {
            _reminder?.Clear();
        }

        var attention = snap.State.NeedsAttention();

        // The icon colour and the tooltip are never gated — they are the state
        // itself, not an interruption. Only the balloon has to ask permission.
        _tray?.Render(snap, notify && _alerts.AllowToast(snap.State));

        // There is something to dismiss exactly when the mascot is still making
        // a fuss about it.
        _dismissable = attention && !_alerts.IsAcknowledged(snap.State);

        // MenuState holds plain text; each menu escapes it for its own toolkit.
        _menuStatus = $"{snap.State.Title()} — {snap.WorkspaceName ?? "workspace"}";

        var park = _alerts.AllowPark(snap.State);
        var speak = _alerts.AllowBubble(snap.State);

        foreach (var member in _crew)
        {
            member.Window.Render(snap);

            // Stopping is the signal: patrol while healthy, go to a corner and
            // hang when there's something the user has to deal with.
            member.Patrol.SetAlert(park);

            // A speech bubble with no mascot under it is just a floating box.
            if (!member.Wanted || _suppressed)
            {
                member.Bubble.Dismiss();
                continue;
            }

            member.Bubble.Track(member.Frame, ScreenBounds());

            if (attention && speak) member.Bubble.Say(snap.State.Title(), snap.Detail, sticky: true);
            else if (notify && speak) member.Bubble.Say(snap.State.Title(), snap.Detail, sticky: false);
            else member.Bubble.Dismiss();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        _watchdog?.Stop();

        // Unsubscribe first. Both handlers post to the dispatcher, so an
        // already-queued one could otherwise run after the tray is disposed and
        // throw ShowBalloonTip into a crash dialog on the way out.
        if (_monitor is not null) _monitor.Changed -= OnStatusChanged;
        if (_sessions is not null) _sessions.Changed -= OnSessionsChanged;
        if (_load is not null) _load.Changed -= OnLoadChanged;
        if (_repoWatch is not null) _repoWatch.DeployMoved -= OnDeployMoved;
        if (_ciWatch is not null)
        {
            _ciWatch.BrokenChanged -= OnBrokenChanged;
            _ciWatch.Finished -= OnPipelinesFinished;
        }

        foreach (var member in _crew) member.Dispose();

        _dashboard?.CloseForShutdown();

        // Notes come down last and stay marked stuck, so the desktop comes back
        // as it was left. The dashboard commits its own edits as it closes;
        // saving again here is the backstop for a shutdown that skipped both.
        _hotkey?.Dispose();
        _stickies?.Dispose();
        _notes?.Save();

        try
        {
            // In parallel: sequential 2s waits could block the UI for much longer.
            Task.WhenAll(
                _sessions?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                _monitor?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                _load?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                _leftovers?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                _repoWatch?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                _ciWatch?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                _logs?.DisposeAsync().AsTask() ?? Task.CompletedTask
            ).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutting down anyway.
        }

        _ci?.Dispose();
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    // ---------- full-screen detection ----------

    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    /// <summary>
    /// The shell already tracks this for notification suppression, so we don't
    /// have to guess from window rectangles.
    /// </summary>
    private static bool IsFullScreenAppRunning()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0
                   && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
