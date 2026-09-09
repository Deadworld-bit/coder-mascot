using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CoderMascot.Core;
using CoderMascot.UI;
using Microsoft.Win32;

namespace CoderMascot;

public partial class App : System.Windows.Application, IDashboardHost, IBranchHost
{
    private Mutex? _single;
    private CoderConfig? _cfg;
    private TrayController? _tray;
    private StatusMonitor? _monitor;
    private SessionMonitor? _sessions;
    private SystemMonitor? _load;
    private LeftoverMonitor? _leftovers;
    private DashboardWindow? _dashboard;
    private Reminder? _reminder;
    private NoteBook? _notes;
    private StickyBoard? _stickies;
    private SetupWindow? _setup;
    private BranchesWindow? _branches;
    private GitReader? _git;

    /// <summary>Last reading per project, so re-opening the window isn't a blank wait.</summary>
    private readonly Dictionary<string, ProjectSnapshot> _repos = new(StringComparer.Ordinal);
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

        _tray = new TrayController([.. _crew.Select(m => m.Window)]);
        _monitor = new StatusMonitor(_cfg);
        // The session watch reaches into the workspace over SSH, so it needs to
        // know when not to bother.
        _sessions = new SessionMonitor(_cfg, () => _monitor?.Latest?.State ?? MascotState.Unknown);
        _load = new SystemMonitor(_cfg);
        _leftovers = new LeftoverMonitor(_cfg);
        _reminder = new Reminder(_cfg.RemindAfterSeconds, _cfg.RemindMaxSeconds);

        _sessions.Changed += OnSessionsChanged;
        _load.Changed += OnLoadChanged;
        _leftovers.Changed += (_, _) => Dispatcher.BeginInvoke(Repaint);
        _monitor.Changed += OnStatusChanged;
        _tray.CheckRequested += (_, _) => _monitor.PollNow();
        _tray.Acknowledged += (_, _) => Acknowledge();
        _tray.AlertPolicyChanged += (_, policy) => SetAlertPolicy(policy);
        _tray.DashboardRequested += (_, _) => ShowDashboard();
        _tray.NoteRequested += (_, _) => NewSticky();
        _tray.SetupRequested += (_, _) => ShowSetup();
        _tray.BranchesRequested += (_, _) => ShowBranches();

        foreach (var member in _crew) Wire(member);

        // Every menu shows the same setting, so seed them all from the file.
        ShowAlertPolicy();

        _tray.VisibilityToggled += (_, ev) =>
        {
            var member = _crew.FirstOrDefault(m => ReferenceEquals(m.Window, ev.Mascot));
            if (member is null) return;

            member.Wanted = ev.Visible;
            member.Patrol.SetEnabled(ev.Visible && _cfg.Patrol && !_suppressed);
            if (!ev.Visible) member.Bubble.Dismiss();
        };

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

        member.Window.CheckRequested += (_, _) => _monitor?.PollNow();

        // The patrol toggle is in every character's own context menu, so it
        // applies to the character you right-clicked, not to all of them.
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
        member.Window.AlertPolicyChanged += (_, policy) => SetAlertPolicy(policy);
        member.Window.DashboardRequested += (_, _) => ShowDashboard();
        member.Window.NoteRequested += (_, _) => NewSticky();
        member.Window.SetupRequested += (_, _) => ShowSetup();
        member.Window.BranchesRequested += (_, _) => ShowBranches();

        // One machine-wide setting shown in several menus: whichever one is used
        // to change it, the others have to stop claiming otherwise.
        member.Window.StartupChanged += (sender, on) =>
        {
            foreach (var other in _crew)
            {
                if (!ReferenceEquals(other.Window, sender)) other.Window.SetStartWithWindows(on);
            }

            _dashboard?.RefreshSettings();
        };
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

        ShowAlertPolicy();
        Repaint();
    }

    /// <summary>Tick the same choice in every menu — each character has its own.</summary>
    private void ShowAlertPolicy()
    {
        _tray?.SetAlertPolicy(_alerts.Policy);
        foreach (var member in _crew) member.Window.SetAlertPolicy(_alerts.Policy);
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

    // ---------- the dashboard ----------

    /// <summary>
    /// Open the detail view, creating it the first time.
    ///
    /// Kept alive once opened rather than rebuilt: it holds the settings fields
    /// and the scroll position, and closing it only hides it.
    /// </summary>
    private void ShowDashboard(bool focusNotes = false)
    {
        if (_dashboard is null)
        {
            _dashboard = new DashboardWindow(this);

            // A closed WPF window cannot be shown again, so forget it and build
            // a fresh one next time rather than throwing on the second open.
            _dashboard.Closed += (_, _) => _dashboard = null;
        }

        _dashboard.Summon(focusNotes);
    }

    /// <summary>
    /// Ask for a Coder address and a token.
    ///
    /// Never more than one of these: opened from a menu while the first-run copy
    /// is already up, a second window would let two of them save different
    /// credentials over each other.
    /// </summary>
    private void ShowSetup()
    {
        if (_cfg is null) return;

        if (_setup is not null)
        {
            _setup.Activate();
            return;
        }

        _setup = new SetupWindow(_cfg);
        _setup.Closed += (_, _) => _setup = null;
        _setup.Connected += (_, _) =>
        {
            // The token is read per request, so the new one is already in use —
            // but the workspace the old one resolved to is not necessarily this
            // account's, and the poll loop is otherwise up to 20 seconds away
            // from noticing any of it.
            _monitor?.Reauthenticate();
            _sessions?.CheckForStall();
            _dashboard?.RefreshSettings();
        };

        _setup.Show();
        _setup.Activate();
    }

    /// <summary>
    /// Where work has got to, across every repository.
    ///
    /// Its own window rather than a dashboard panel: it answers a different
    /// question on a different clock — the dashboard is about right now, this is
    /// about the last few weeks — and it needs the width.
    /// </summary>
    private void ShowBranches()
    {
        if (_branches is null)
        {
            _branches = new BranchesWindow(this);
            _branches.Closed += (_, _) => _branches = null;
        }

        _branches.Summon();
    }

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
        // Branches window, where a fetch failure still leaves a real reading on
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
    /// Only ever on the user's say-so, wherever it is called from: this talks to
    /// the network, can ask for credentials, and on the first fetch of a
    /// URL-only repository is minutes of real work — none of which belongs on a
    /// window opening.
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
        // Same guard as the mascot's menu: UseShellExecute means "ask the shell
        // what this string is", so only a parsed http(s) URI may reach it.
        if (_cfg?.Url is not { } url ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;

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

    void IDashboardHost.ShowSetup() => ShowSetup();

    void IDashboardHost.SetAlertPolicy(AlertPolicy policy) => SetAlertPolicy(policy);

    string? IDashboardHost.SetStartWithWindows(bool on)
    {
        if (_cfg is null) return "Not ready yet.";

        var error = StartupRegistration.Set(on);
        if (error is not null) return error;

        _cfg.StartWithWindows = on;
        _cfg.Save();

        // Every menu shows the same switch, so none of them may be left lying.
        foreach (var member in _crew) member.Window.SetStartWithWindows(on);
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
            foreach (var member in _crew)
                member.Window.SetSessionWatchMissing(s.Ok && !s.Installed);

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
        var dismissable = attention && !_alerts.IsAcknowledged(snap.State);
        _tray?.SetDismissable(dismissable);

        var park = _alerts.AllowPark(snap.State);
        var speak = _alerts.AllowBubble(snap.State);

        foreach (var member in _crew)
        {
            member.Window.Render(snap);
            member.Window.SetDismissable(dismissable);

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

        foreach (var member in _crew) member.Dispose();

        _dashboard?.Close();

        // Notes come down last and stay marked stuck, so the desktop comes back
        // as it was left. The dashboard commits its own edits as it closes;
        // saving again here is the backstop for a shutdown that skipped both.
        _branches?.Close();
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
                _leftovers?.DisposeAsync().AsTask() ?? Task.CompletedTask
            ).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutting down anyway.
        }

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
