using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CoderMascot.Core;

using Brush = System.Windows.Media.Brush;

namespace CoderMascot.UI;

/// <summary>What the branch view needs from the app.</summary>
public interface IBranchHost
{
    IReadOnlyList<GitProject> Projects { get; }

    /// <summary>Read one repository. `fetch` first talks to the remote.</summary>
    Task<ProjectSnapshot> ReadAsync(GitProject project, bool fetch, CancellationToken ct);

    /// <summary>The last reading, if there is one, so re-opening isn't a blank window.</summary>
    ProjectSnapshot? Cached(GitProject project);

    /// <summary>
    /// The repositories exactly as the config file holds them.
    ///
    /// The editor works on these rather than on the validated
    /// <see cref="GitProject"/>s, so that saving cannot quietly drop a field the
    /// window happens not to display.
    /// </summary>
    IReadOnlyList<ProjectEntry> Configured { get; }

    /// <summary>Write the list back to the config. Returns what was kept.</summary>
    IReadOnlyList<GitProject> SaveProjects(IReadOnlyList<ProjectEntry> entries);

    /// <summary>Group headings the rail is showing folded shut, by group key.</summary>
    IReadOnlyList<string> Collapsed { get; }

    /// <summary>Remember the folded headings, so they are still folded tomorrow.</summary>
    void SetCollapsed(IEnumerable<string> keys);

    /// <summary>
    /// Is there a repository at that path, and what are its branches called?
    ///
    /// `fetch` is what makes a URL-only repository readable the first time: it
    /// downloads the copy. Off by default, and only ever true because somebody
    /// pressed a button that says so.
    /// </summary>
    Task<ProbeReport> ProbeAsync(GitProject project, bool fetch, CancellationToken ct);

    /// <summary>Checkouts worth offering in the folder box, so it isn't typed blind.</summary>
    Task<IReadOnlyList<string>> DiscoverAsync(RepoHost host, CancellationToken ct);

    /// <summary>
    /// Give Git a username and token for a private server. Null when it worked.
    ///
    /// It goes into Git's own credential store, not into this app's settings —
    /// so it is kept the way the rest of the machine keeps secrets, and every
    /// other git tool can use it too.
    /// </summary>
    Task<string?> SignInAsync(GitProject project, string username, string secret, CancellationToken ct);

    /// <summary>
    /// Can the server be reached and signed in to? Downloads nothing.
    ///
    /// The question that separates a bad URL from a bad network from a bad
    /// sign-in from an account without access — four failures that otherwise
    /// arrive looking identical.
    /// </summary>
    Task<RemoteTest> TestAsync(GitProject project, CancellationToken ct);
}

public sealed record ProjectRow(string Id, string Name, string Sub, string Tip,
                                string Badge, Visibility BadgeShown, Thickness Indent,
                                bool Selected, Brush Bar, Brush BadgeBack, Brush BadgeInk);

/// <summary>
/// A heading in the repository pane, which is also the control that folds it.
///
/// The open/shut triangle is a drawn shape picked by visibility rather than a
/// character in a font, because the one control here that hides half the list is
/// the last one that should depend on a glyph being present.
/// </summary>
public sealed record GroupBar(string Key, string Title, string Count, Visibility CountShown, string Tip,
                              Visibility ShutShown, Visibility OpenShown,
                              string Badge, Visibility BadgeShown,
                              Brush Ink, bool Selected, Brush Bar, Brush BadgeBack, Brush BadgeInk);

/// <summary>A heading inside the branch list, with how many are under it.</summary>
public sealed record GroupHeader(string Title, string Count, string Note, Brush Accent);

/// <summary>
/// A count, which is also the filter for the thing it counts.
///
/// Reading "12 not shipped" and then hunting for a control that shows you those
/// twelve is two steps where there is no reason for two.
/// </summary>
public sealed record SummaryChip(BranchGroup? Key, string Count, string Text, string Tip,
                                 Brush Back, Brush Ink, Brush Edge);

public sealed record TargetCard(string Environment, string Branch, string Last, Brush Accent);

public sealed record StandingPill(string Text, string Tip, Brush Back, Brush Ink);

public sealed record BranchRow(string Name, string Sub, string Counts, string CountsTip,
                               Brush Accent, Brush Back,
                               string TargetLabel, Visibility TargetShown,
                               string SideLabel, Visibility SideShown,
                               IReadOnlyList<StandingPill> Pills, TraceMark Traced);

/// <summary>
/// Whether one branch is the one the timeline is filtered to.
///
/// The one thing about a row that changes without the list being rebuilt:
/// rebuilding it would throw away the button that was just pressed, and the
/// keyboard focus with it.
/// </summary>
public sealed class TraceMark(bool marked) : INotifyPropertyChanged
{
    private bool _on = marked;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool On
    {
        get => _on;
        set
        {
            if (_on == value) return;

            _on = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(On)));
        }
    }
}

public sealed record LandingRow(string Day, Visibility DayShown, string Source, string When,
                                string Detail, Brush Accent, double Fade);

/// <summary>A linked diagram: press to open it, right-click to change it.</summary>
public sealed record DiagramChip(string Target, string Title, string Kind, string Tip);

/// <summary>
/// Where work has got to, across every repository.
///
/// The question this page exists for is one you cannot answer from a branch
/// list: *is the thing I wrote three weeks ago in what the server is running?*
/// Git can answer it, but only by asking it several questions per branch and
/// holding the answers in your head — which is exactly what stops being possible
/// once there are four repositories and thirty branches.
///
/// So the reading order is fixed: what is deployed, then every branch measured
/// against those, then the landings in time order. Colour is never the only
/// signal — every pill says "staging · 2d ago" or "production · not yet" in
/// words, because a room full of green and grey dots is a puzzle, not an answer.
/// </summary>
public partial class ProjectsPage : System.Windows.Controls.UserControl
{
    private readonly IBranchHost _host;
    private readonly IDrawerHost _drawer;

    private GitProject? _project;
    private ProjectSnapshot? _snapshot;
    private CancellationTokenSource? _reading;

    /// <summary>Timeline filtered to one branch, from the Trace button.</summary>
    private string? _tracing;

    /// <summary>Setting a control from code, so its handler doesn't read into it.</summary>
    private bool _quiet;

    /// <summary>Which deploy branch the timeline is showing.</summary>
    private string? _timelineTarget;

    /// <summary>Which summary chip is pressed, or null for everything.</summary>
    private BranchGroup? _view;

    /// <summary>The first time the page is shown it reads; after that it keeps what it has.</summary>
    private bool _started;

    /// <summary>The page has been laid out once, so the timeline's starting state has been chosen.</summary>
    private bool _sized;

    private bool _timelineOn = true;

    /// <summary>Where the timeline column was last dragged to, for when it comes back.</summary>
    private GridLength _timelineWidth = new(360);

    /// <summary>The right-hand pane has room to put Deployed beside Diagrams.</summary>
    private bool _roomy = true;

    private bool _repoWide = true;

    /// <summary>The page is this wide or wider the first time it is laid out: the timeline starts open.</summary>
    private const double TimelineOnFrom = 1240;

    /// <summary>Below this pane width Diagrams drop under Deployed.</summary>
    private const double SideBySideFrom = 720;

    /// <summary>What the branches keep beside an open timeline, while the page can afford it.</summary>
    private const double BranchesFloor = 340;

    /// <summary>
    /// How the branches are grouped, in the order the groups appear.
    ///
    /// The order is the order of the question being asked: what hasn't got
    /// anywhere, then what half has, then what could not be established, then
    /// the things that are finished — and the deploy branches themselves last.
    /// The Deployed band at the top of the page already says what is live, so
    /// they come after the work measured against them, not before it.
    /// A flat list sorted by date puts a branch that shipped in March directly
    /// above one that has never shipped at all, and reading it is arithmetic.
    /// </summary>
    private sealed record Bucket(BranchGroup Key, string Title, string Note, string Accent);

    /// <summary>
    /// The groups, in the order the question is asked — which is not the order
    /// of the enum, and is the only thing this list decides: the deploy branches
    /// come last, after everything measured against them. The summary chips
    /// follow the same order. What belongs in each group is
    /// <see cref="BranchLine.Group"/>'s business.
    /// </summary>
    private static readonly Bucket[] Buckets =
    [
        new(BranchGroup.Open, "NOT SHIPPED ANYWHERE", "hasn't reached any deploy branch", Tone.Open),
        new(BranchGroup.Partly, "PARTLY SHIPPED", "in some environments, not all", Tone.Partly),
        new(BranchGroup.Unknown, "COULDN'T CHECK", "unknown, which is not the same as no", Tone.Unsure),
        new(BranchGroup.Shipped, "SHIPPED EVERYWHERE", "", Tone.Shipped),
        new(BranchGroup.Tidy, "SAFE TO DELETE", "merged everywhere, and the remote is gone", Tone.Tidy),
        new(BranchGroup.Deploy, "DEPLOY BRANCHES", "what everything above is measured against", Tone.Deploy),
    ];

    public ProjectsPage(IBranchHost host, IDrawerHost drawer)
    {
        _host = host;
        _drawer = drawer;
        InitializeComponent();

        // Not on Loaded: that fires for a page nobody has opened, and a git read
        // every time the main window appears is work for nothing. Later visits
        // keep what is on screen; F5 and Re-read are there for a fresh look.
        IsVisibleChanged += (_, e) =>
        {
            if (_started || !(bool)e.NewValue) return;

            _started = true;
            SortBox.SelectedIndex = 0;
            Start();
        };
    }

    /// <summary>Stop any read still running. The main window calls this as it closes.</summary>
    public void Stop() => _reading?.Cancel();

    private void Start() => Reload(null);

    /// <summary>
    /// Take the project list as it now stands, and land on one of them.
    ///
    /// Called on open and after every edit, because both are the same question:
    /// this is the list, which row are we looking at? Naming the wanted project
    /// rather than an index is what survives the row being renamed, moved, or —
    /// in the case of a folder that was corrected — replaced outright.
    /// </summary>
    private void Reload(string? wanted)
    {
        var projects = _host.Projects;

        NoProjects.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyCard.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (projects.Count == 0)
        {
            _reading?.Cancel();
            _project = null;
            _snapshot = null;

            PaintProjects();
            PaintAll();
            PaintDiagrams();
            Busy(null);
            return;
        }

        var pick = projects.FirstOrDefault(p => p.Id == wanted) ?? projects[0];

        // Forced: the deploy branches may have just changed, and every standing
        // in the cached reading was measured against the old ones.
        Select(pick, force: true);
    }

    // ---------- projects ----------

    private void PaintProjects()
    {
        var rows = new List<object>();
        var bands = ProjectGroups.Arrange(_host.Projects, p => p.Group);
        var folded = _host.Collapsed.ToHashSet(StringComparer.Ordinal);
        var query = RepoSearch.Text.Trim();
        var searching = query.Length > 0;
        var found = 0;

        foreach (var band in bands)
        {
            // A search keeps what matches and drops the groups with nothing in
            // them. The fold is not touched: it is only ignored while the box has
            // text, so clearing it brings the list back as it was saved.
            IReadOnlyList<GitProject> items = searching
                ? band.Items.Where(p => Matches(p, query)).ToList()
                : band.Items;
            if (items.Count == 0) continue;

            // Only a named band can be folded. The leftovers are the bottom of
            // the list, and a heading you can hide the whole list behind is a
            // way to end up with an empty rail and no idea why.
            var shut = !searching && band.Named && folded.Contains(band.Key);

            if (band.Named) rows.Add(Heading(band, items, shut, searching));
            if (shut) continue;

            found += items.Count;
            foreach (var project in items) rows.Add(Line(project, indented: band.Named));
        }

        ProjectList.ItemsSource = rows;

        NoMatch.Visibility = searching && found == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Text = searching ? $"No repository matches “{query}”." : string.Empty;
    }

    private static bool Matches(GitProject p, string query) =>
        p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || p.Path.Contains(query, StringComparison.OrdinalIgnoreCase)
        || p.Group?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private void OnRepoSearchChanged(object sender, TextChangedEventArgs e) => PaintProjects();

    private GroupBar Heading(ProjectBand<GitProject> band, IReadOnlyList<GitProject> items, bool shut, bool searching)
    {
        var read = items.Select(_host.Cached).ToList();
        var broken = read.Any(s => s is { Ok: false });
        var outstanding = read.Sum(s => s is { Ok: true } ? s.Outstanding : 0);
        var holds = _project is not null && items.Any(p => p.Id == _project.Id);

        return new GroupBar(
            Key: band.Key,
            Title: band.Title,
            Count: items.Count.ToString(CultureInfo.InvariantCulture),

            // Only while the group is shut. Open, you can see how many there are
            // by looking, and the number sat beside the outstanding badge as a
            // second unexplained figure that shifted its neighbour as it came
            // and went.
            CountShown: shut ? Visibility.Visible : Visibility.Collapsed,
            ShutShown: shut ? Visibility.Visible : Visibility.Collapsed,
            OpenShown: shut ? Visibility.Collapsed : Visibility.Visible,
            Tip: searching
                ? "Every group stays open while you search."
                : shut
                    ? $"{items.Count} hidden. Click to open this group."
                    : "Click to fold this group away.",

            // The roll-up stays on the heading in both states. A group folded
            // shut on something that needs attention, with nothing to say so, is
            // the fold quietly costing you the thing the badge is for.
            Badge: broken ? "!" : outstanding.ToString(CultureInfo.InvariantCulture),
            BadgeShown: broken || outstanding > 0 ? Visibility.Visible : Visibility.Collapsed,
            Ink: Paint(shut ? Tone.Faint : Tone.Ink2),
            Selected: shut && holds,
            Bar: shut && holds ? Paint(Tone.Pick) : System.Windows.Media.Brushes.Transparent,
            BadgeBack: Paint(broken ? Tone.BadEdge : Tone.Lift),
            BadgeInk: Paint(broken ? Tone.BadInk : Tone.Ink));
    }

    private ProjectRow Line(GitProject p, bool indented)
    {
        var snap = _host.Cached(p);
        var chosen = _project is not null && p.Id == _project.Id;
        var outstanding = snap?.Ok == true ? snap.Outstanding : 0;

        return new ProjectRow(
            Id: p.Id,
            Name: p.Name,
            Sub: p.Host switch
            {
                RepoHost.Workspace => "workspace · " + Tail(p.Path),
                RepoHost.Remote => "git url · " + Tail(p.Path),
                _ => "this pc · " + Tail(p.Path),
            },
            Tip: p.Path,
            Badge: snap is null ? "" : snap.Ok ? outstanding.ToString(CultureInfo.InvariantCulture) : "!",
            BadgeShown: snap is null || (snap.Ok && outstanding == 0) ? Visibility.Collapsed : Visibility.Visible,

            // Under a heading it steps in, so the heading reads as covering it
            // rather than as one more row. Ungrouped rows line up with the label
            // above the list.
            Indent: indented ? new Thickness(27, 7, 0, 7) : new Thickness(12, 7, 0, 7),

            // Which row you are on, said with a mark at the edge as well as a
            // slightly lighter fill: on a dark theme, two greys four steps
            // apart is not a signal anybody can rely on.
            Selected: chosen,
            Bar: chosen ? Paint(Tone.Pick) : System.Windows.Media.Brushes.Transparent,
            BadgeBack: Paint(snap?.Ok == false ? Tone.BadEdge : Tone.Lift),
            BadgeInk: Paint(snap?.Ok == false ? Tone.BadInk : Tone.Ink));
    }

    /// <summary>
    /// Fold a heading, or open it.
    ///
    /// Saved straight away rather than on close: the window is closed by being
    /// dismissed, and a preference that only survives a tidy exit is one that
    /// mostly doesn't.
    /// </summary>
    private void OnToggleGroup(object sender, RoutedEventArgs e)
    {
        if (Row<GroupBar>(sender) is not { } bar) return;

        // Every group is open while the search box has text, so a fold made now
        // would be saved without ever being seen.
        if (RepoSearch.Text.Trim().Length > 0) return;

        var folded = _host.Collapsed.ToHashSet(StringComparer.Ordinal);
        if (!folded.Remove(bar.Key)) folded.Add(bar.Key);

        _host.SetCollapsed(folded);
        PaintProjects();
    }

    private void OnPickProject(object sender, RoutedEventArgs e)
    {
        if (Row<ProjectRow>(sender) is not { } row) return;

        // By id, not by name: two checkouts of the same repository in different
        // folders both take the folder's name, and picking by name would always
        // select the first of them.
        if (_host.Projects.FirstOrDefault(p => p.Id == row.Id) is not { } project) return;
        if (_project is not null && project.Id == _project.Id) return;

        Select(project);
    }

    private void Select(GitProject project, bool force = false)
    {
        _project = project;
        _tracing = null;
        _timelineTarget = null;

        // A filter belongs to the repository it was typed for. Carrying "only
        // what hasn't shipped" across to the next project silently hides most of
        // it, and the hiding looks like the project being empty.
        _view = null;

        _quiet = true;
        BranchFilter.Text = string.Empty;
        _quiet = false;

        DiagramProblem(null);

        // Whatever was read last time goes up immediately — a window that starts
        // blank for eight seconds while ssh connects reads as broken.
        _snapshot = _host.Cached(project);
        PaintProjects();
        PaintAll();

        // Painted on selection and on save only, not with every reading: a
        // finished read replacing the chips under an open right-click menu
        // turns its Edit and Remove into silent no-ops.
        PaintDiagrams();

        Read(fetch: false, force: force || _snapshot is null);
    }

    // ---------- reading ----------

    private async void Read(bool fetch, bool force)
    {
        if (_project is not { } project) return;

        // Already read a moment ago: nothing to do, but the buttons must still
        // be usable — an early return that leaves them disabled strands the
        // window on whatever note was last set.
        if (!force && !fetch && _snapshot is not null &&
            (DateTimeOffset.Now - _snapshot.ReadAt).TotalMinutes < 2)
        {
            Busy(null);
            return;
        }

        _reading?.Cancel();
        _reading = new CancellationTokenSource();
        var token = _reading.Token;

        Busy(fetch ? $"Fetching {project.Name}…" : $"Reading {project.Name}…");

        ProjectSnapshot? snapshot = null;
        try
        {
            snapshot = await _host.ReadAsync(project, fetch, token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer read, which owns the busy state now.
        }
        catch (Exception ex)
        {
            CrashLog.Write("branches", ex);
            snapshot = new ProjectSnapshot
            {
                Name = project.Name,
                Path = project.Path,
                Host = project.Host,
                Deployed = project.Deployed,
                Problem = ex.Message,
            };
        }
        finally
        {
            // Whoever is still the current project clears the note. Without
            // this, an abandoned read leaves "Reading CONTRACT-APP…" and two dead
            // buttons on screen for good.
            if (!token.IsCancellationRequested && _project?.Id == project.Id) Busy(null);
        }

        // A slower read for a project already navigated away from must not
        // overwrite the one on screen.
        if (snapshot is null || token.IsCancellationRequested || _project?.Id != project.Id) return;

        _snapshot = snapshot;
        PaintProjects();
        PaintAll();
    }

    private void Busy(string? note)
    {
        BusyNote.Text = note ?? string.Empty;
        BtnRead.IsEnabled = note is null && _project is not null;
        BtnFetch.IsEnabled = note is null && _project is not null;

        // Editable while a read is in flight: the usual reason to open the
        // editor is that the read is failing, and waiting out a 45-second ssh
        // timeout before being allowed to fix the path is its own small cruelty.
        BtnEdit.IsEnabled = _project is not null;
    }

    // ---------- adding and editing ----------

    private async void OnAddProject(object sender, RoutedEventArgs e)
    {
        // Where the last one lived is the best guess at where this one does:
        // repositories arrive in runs, and someone adding their third local
        // clone should not have to reach for the same radio button each time.
        var host = _host.Configured.LastOrDefault()?.ToProject().Host ?? RepoHost.Remote;

        var draft = ProjectDraft.Blank(host);

        // And into the group you are already looking at. Adding the fourth
        // repository of a group and then having to name that group again is the
        // kind of retype that leaves you with "Backend" and "backend".
        draft.Group = _project?.Group ?? string.Empty;

        await Edit(draft);
    }

    private async void OnEditProject(object sender, RoutedEventArgs e)
    {
        if (_project is not { } project) return;

        // Straight from the file, not rebuilt out of what this window happens to
        // be holding: anything in the entry the window never displays would
        // otherwise be dropped by the act of opening and saving the editor.
        var entry = _host.Configured.FirstOrDefault(x => x.ToProject().Id == project.Id);
        if (entry is null) return;

        await Edit(ProjectDraft.From(entry));
    }

    private async Task Edit(ProjectDraft draft)
    {
        var taken = _host.Configured
            .Select(x => x.ToProject())
            .Where(p => !string.Equals(p.Id, draft.Replacing, StringComparison.Ordinal))
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var form = new ProjectForm(_host, draft, taken);

        // False when the drawer was closed on it, or the window went while it was open.
        if (!await _drawer.OpenAsync(form) || !IsLoaded) return;

        var entries = _host.Configured.ToList();
        var at = draft.Replacing is null
            ? -1
            : entries.FindIndex(x => string.Equals(x.ToProject().Id, draft.Replacing, StringComparison.Ordinal));

        string? wanted = null;

        if (form.Removed)
        {
            if (at >= 0) entries.RemoveAt(at);
        }
        else if (form.Result is { } saved)
        {
            // Replaced in place, so correcting a folder doesn't send the row to
            // the bottom of a list you had in a deliberate order.
            if (at >= 0) entries[at] = saved;
            else entries.Add(saved);

            wanted = saved.ToProject().Id;
        }

        _host.SaveProjects(entries);
        Reload(wanted);
    }

    // ---------- diagrams ----------

    private void PaintDiagrams()
    {
        if (_project is not { } project)
        {
            DiagramStrip.Visibility = Visibility.Collapsed;
            ArrangeBand();
            return;
        }

        DiagramStrip.Visibility = Visibility.Visible;
        ArrangeBand();

        var chips = project.Diagrams
            .Select(d => DiagramLink.Inspect(d.Target).Place is { } place
                ? new DiagramChip(d.Target ?? place.Target, d.Label,
                                  place.Kind == DiagramKind.File ? "file" : "link",
                                  place.Target + "\nRight-click to edit or remove.")
                : null)
            .OfType<DiagramChip>()
            .ToList();

        DiagramList.ItemsSource = chips;
        DiagramsEmpty.Text = "Nothing linked yet. Use + Add for this project's database diagram, or any other draw.io file.";
        DiagramsEmpty.Visibility = chips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DiagramProblem(string? problem)
    {
        DiagramNote.Text = problem ?? string.Empty;
        DiagramNote.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnOpenDiagram(object sender, RoutedEventArgs e)
    {
        if (Row<DiagramChip>(sender) is not { } chip) return;

        var asked = _project?.Id;
        DiagramProblem(null);

        var problem = await Task.Run(() => DesktopActions.OpenDiagram(chip.Target));

        // Another project may be on screen by now, and its strip is not where this belongs.
        if (_project?.Id == asked) DiagramProblem(problem);
    }

    private async void OnAddDiagram(object sender, RoutedEventArgs e)
    {
        if (_project is not { } project) return;
        if (await AskDiagram(project, null) is not { } added) return;

        SaveDiagrams(project, [.. project.Diagrams, added]);
    }

    private async void OnEditDiagram(object sender, RoutedEventArgs e)
    {
        if (_project is not { } project || Row<DiagramChip>(sender) is not { } chip) return;

        var list = project.Diagrams.ToList();
        var at = list.FindIndex(d => d.Target == chip.Target);
        if (at < 0)
        {
            DiagramProblem("That link has changed since the list was drawn. Try again.");
            PaintDiagrams();
            return;
        }

        if (await AskDiagram(project, list[at]) is not { } changed) return;

        list[at] = changed;
        SaveDiagrams(project, list);
    }

    private void OnRemoveDiagram(object sender, RoutedEventArgs e)
    {
        if (_project is not { } project || Row<DiagramChip>(sender) is not { } chip) return;

        var sure = Ask(
            $"Remove the link to “{chip.Title}”?\n\nOnly the link goes — the diagram itself is not touched.",
            "Remove diagram",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        if (sure != MessageBoxResult.OK) return;

        SaveDiagrams(project, project.Diagrams.Where(d => d.Target != chip.Target));
    }

    private async Task<DiagramLink?> AskDiagram(GitProject project, DiagramLink? editing)
    {
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var link in project.Diagrams)
        {
            if (ReferenceEquals(link, editing) || DiagramLink.Inspect(link.Target).Place is not { } place) continue;
            taken.TryAdd(DiagramLink.Key(place), link.Label);
        }

        var form = new DiagramForm(editing, taken);

        // Null when the drawer was closed on it, or the window went while it was open.
        return await _drawer.OpenAsync(form) && IsLoaded ? form.Result : null;
    }

    private void SaveDiagrams(GitProject project, IEnumerable<DiagramLink> diagrams)
    {
        var entries = _host.Configured.ToList();
        var at = entries.FindIndex(x => x.ToProject().Id == project.Id);
        if (at < 0)
        {
            DiagramProblem("This repository is no longer in the list, so the change wasn't saved.");
            return;
        }

        entries[at] = entries[at].WithDiagrams(diagrams);

        // Only the links changed, so there is no re-read: going back to git, or
        // over ssh, to draw one more chip would put the window into "Reading…"
        // for nothing.
        _project = _host.SaveProjects(entries).FirstOrDefault(p => p.Id == project.Id) ?? project;

        DiagramProblem(null);
        PaintDiagrams();
    }

    private void OnReRead(object sender, RoutedEventArgs e) => Read(fetch: false, force: true);

    private void OnFetch(object sender, RoutedEventArgs e) => Read(fetch: true, force: true);

    // ---------- painting ----------

    private void PaintAll()
    {
        PaintHeader();
        PaintTargets();
        PaintChips();
        PaintBranches();
        PaintTimelineTargets();
        PaintTimeline();
    }

    private void PaintHeader()
    {
        if (_project is not { } project)
        {
            var none = _host.Projects.Count == 0;

            HeadTitle.Text = none ? "No repositories yet" : "Branches";
            HeadDetail.Text = none
                ? "Add the ones you ship from, and this page can tell you what has reached them."
                : string.Empty;

            // Both cards belong to a project. Leaving the last one's failure up
            // after it has been removed reports a repository that isn't there.
            ProblemCard.Visibility = Visibility.Collapsed;
            WarningCard.Visibility = Visibility.Collapsed;
            return;
        }

        HeadTitle.Text = project.Name;

        var facts = new List<string> { project.Path };

        if (_snapshot is { Ok: true } snap)
        {
            if (snap.CurrentBranch is { Length: > 0 } current) facts.Add($"on {current}");
            if (snap.DirtyFiles > 0) facts.Add($"{snap.DirtyFiles} uncommitted");
            facts.Add($"read {Friendly.Since(snap.ReadAt)}");
        }

        HeadDetail.Text = string.Join("   ·   ", facts);

        ProblemCard.Visibility = _snapshot is { Ok: false } ? Visibility.Visible : Visibility.Collapsed;
        ProblemText.Text = _snapshot?.Problem ?? string.Empty;

        var warning = _snapshot is { Ok: true } ? _snapshot.Warning : null;
        WarningCard.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = warning ?? string.Empty;
    }

    private void PaintTargets()
    {
        if (_snapshot is not { Ok: true } snap || snap.Deployed.Count == 0)
        {
            TargetList.ItemsSource = null;

            // No deploy branches means no reference, and an empty section with a
            // heading over it is just a thing to wonder about.
            DeployedStrip.Visibility = Visibility.Collapsed;
            ArrangeBand();
            return;
        }

        DeployedStrip.Visibility = Visibility.Visible;
        ArrangeBand();

        // The branch the counts are measured against leads, so the card order and
        // the arrows on every row below agree with each other.
        var primary = _project?.PrimaryTarget;

        TargetList.ItemsSource = snap.Deployed
            .OrderByDescending(kv => string.Equals(kv.Key, primary, StringComparison.Ordinal))
            .Select(kv =>
        {
            var branch = snap.Branches.FirstOrDefault(b => b.Name == kv.Key);
            var landed = snap.Merges.Where(m => m.Target == kv.Key).OrderByDescending(m => m.At).FirstOrDefault();

            var last = branch is null
                ? "This branch isn't in the repository"
                : landed is null
                    ? $"{Friendly.Since(branch.Updated)} · {branch.Author}"
                    : $"last landing {Friendly.Since(landed.At)} · {landed.Source}";

            return new TargetCard(
                Environment: kv.Value,
                Branch: kv.Key,
                Last: last,
                Accent: Paint(branch is null ? Tone.Unsure : Tone.Shipped));
        }).ToList();
    }

    /// <summary>
    /// The counts, which are also the filters.
    ///
    /// Only what exists is offered: a chip reading "0 partly shipped" is a
    /// control that does nothing, and a row of them trains you to stop reading
    /// the row.
    /// </summary>
    private void PaintChips()
    {
        if (_snapshot is not { Ok: true } snap || snap.Branches.Count == 0)
        {
            SummaryChips.ItemsSource = null;
            return;
        }

        var chips = new List<SummaryChip>
        {
            Chip(null, snap.Branches.Count, "in all", "Every branch in this repository", Tone.Faint),
        };

        if (snap.Deployed.Count > 0)
        {
            Add(BranchGroup.Open, "not shipped", "Hasn't reached any deploy branch", Tone.OpenInk);
            Add(BranchGroup.Partly, "partly shipped", "In some environments, not all", Tone.PartlyInk);
            Add(BranchGroup.Unknown, "couldn't check", "Unknown, which is not the same as no", Tone.UnsureInk);
            Add(BranchGroup.Tidy, "safe to delete", "Merged everywhere, and the remote is gone", Tone.TidyInk);
        }

        SummaryChips.ItemsSource = chips;

        void Add(BranchGroup key, string text, string tip, string ink)
        {
            var count = snap.Branches.Count(b => b.Group == key);
            if (count > 0) chips.Add(Chip(key, count, text, tip, ink));
        }
    }

    private SummaryChip Chip(BranchGroup? key, int count, string text, string tip, string ink)
    {
        // "in all" is a chip like the others, and it is the one that is lit when
        // nothing is filtered — so the row always shows which view you are in,
        // rather than showing nothing and leaving you to infer it.
        var on = _view == key;

        return new SummaryChip(
            Key: key,
            Count: count.ToString(CultureInfo.InvariantCulture),
            Text: text,
            Tip: on && key is not null ? tip + " — press again to show everything" : tip,
            Back: Paint(on ? Tone.PickTint : Tone.Row),
            Ink: Paint(on ? Tone.Ink : ink),
            Edge: Paint(on ? Tone.Pick : Tone.Hair));
    }

    private void OnPickChip(object sender, RoutedEventArgs e)
    {
        if (Row<SummaryChip>(sender) is not { } chip) return;

        // Pressing the one already on turns it off, so there is always a way
        // back to everything without hunting for an "all" button.
        _view = _view == chip.Key ? null : chip.Key;

        PaintChips();
        PaintBranches();
    }

    private void OnSortChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_quiet) return;
        PaintBranches();
    }

    private void PaintBranches()
    {
        if (_snapshot is not { Ok: true } snap)
        {
            BranchList.ItemsSource = null;
            BranchesEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        var query = BranchFilter.Text.Trim();

        var matching = snap.Branches
            .Where(b => _view is null || b.Group == _view)
            .Where(b => query.Length == 0
                        || b.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || b.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || b.Subject.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sorted = Sorted(matching);

        // Without deploy branches there is nothing to group *by*: every branch
        // would fall into "not shipped anywhere", which is true only in the sense
        // that the question was never asked.
        var items = snap.Deployed.Count == 0
            ? [.. sorted.Select(Row).Cast<object>()]
            : Grouped(sorted);

        BranchList.ItemsSource = items;

        BranchesEmpty.Visibility = matching.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BranchesEmpty.Text = snap.Branches.Count == 0
            ? "No branches."
            : query.Length > 0
                ? $"No branch matches \u201c{query}\u201d."
                : _view switch
                {
                    BranchGroup.Open => "Everything has reached at least one deploy branch.",
                    BranchGroup.Partly => "Nothing is half-shipped \u2014 every branch is all the way in, or not in at all.",
                    BranchGroup.Unknown => "Every branch was checked successfully.",
                    BranchGroup.Tidy => "Nothing to tidy up.",
                    _ => "No branches.",
                };
    }

    /// <summary>
    /// In the order of <see cref="Buckets"/>: what hasn't got anywhere, what half
    /// has, what couldn't be established, the things that are finished — and the
    /// deploy branches last.
    /// </summary>
    private List<object> Grouped(IReadOnlyList<BranchLine> branches)
    {
        var items = new List<object>();

        foreach (var bucket in Buckets)
        {
            var inside = branches.Where(b => b.Group == bucket.Key).ToList();
            if (inside.Count == 0) continue;

            // No heading when a filter has already reduced the list to one
            // group: it would be a label for the whole of what is on screen.
            if (_view is null)
            {
                items.Add(new GroupHeader(
                    Title: bucket.Title,
                    Count: inside.Count.ToString(CultureInfo.InvariantCulture),
                    Note: bucket.Note,
                    Accent: Paint(bucket.Accent)));
            }

            items.AddRange(inside.Select(Row).Cast<object>());
        }

        return items;
    }

    private List<BranchLine> Sorted(IReadOnlyList<BranchLine> branches) =>
        SortBox.SelectedIndex switch
        {
            1 => [.. branches.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)],

            // Nulls last: a repository whose git is too old for the counts would
            // otherwise sort every branch to the top and call it an ordering.
            2 => [.. branches.OrderByDescending(b => b.Behind ?? -1)
                             .ThenByDescending(b => b.Updated)],
            3 => [.. branches.OrderBy(b => b.Author, StringComparer.OrdinalIgnoreCase)
                             .ThenByDescending(b => b.Updated)],
            _ => [.. branches.OrderByDescending(b => b.Updated)],
        };

    /// <summary>
    /// Ctrl+F to the filter, Esc to clear it, F5 to read again.
    ///
    /// The three things you do most, without reaching for the mouse. The main
    /// window passes every key here, ahead of the controls, while this page is
    /// showing — so they work with the focus in the filter box, or in the nav.
    /// </summary>
    internal void OnShortcut(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F5)
        {
            if (BtnRead.IsEnabled) OnReRead(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.F &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            BranchFilter.Focus();
            BranchFilter.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key != System.Windows.Input.Key.Escape) return;

        // In the repository search, Escape is the search's own.
        if (RepoSearch.IsKeyboardFocused)
        {
            if (RepoSearch.Text.Length > 0)
            {
                RepoSearch.Clear();
                e.Handled = true;
            }

            return;
        }

        // Escape undoes the narrowing, in the order it was most likely applied.
        // It never closes anything: the window is not this page's to close.
        if (BranchFilter.Text.Length > 0)
        {
            BranchFilter.Text = string.Empty;
            e.Handled = true;
        }
        else if (_tracing is not null)
        {
            OnClearTrace(sender, e);
            e.Handled = true;
        }
        else if (_view is not null)
        {
            _view = null;
            PaintChips();
            PaintBranches();
            e.Handled = true;
        }
    }

    private BranchRow Row(BranchLine branch)
    {
        // Deploy branches are the reference, not a thing to be measured; work
        // that has reached everywhere is done; everything else is outstanding,
        // and partly-landed is its own answer worth seeing at a distance.
        var accent = branch.IsTarget ? Tone.Deploy
            : branch.HasUnknowns ? Tone.Unsure
            : branch.MergedEverywhere ? Tone.Shipped
            : branch.MergedSomewhere ? Tone.Partly
            : Tone.Open;

        var counts = branch.Ahead is { } ahead && branch.Behind is { } behind
            ? $"↑{ahead}  ↓{behind}"
            : string.Empty;

        // Named, not "the first deploy branch": which one that is was invisible,
        // and a number whose reference point you can't see is not a measurement.
        var countsTip = _project?.PrimaryTarget is { Length: > 0 } against
            ? $"Commits ahead of / behind {against}"
            : "Commits ahead of / behind the deploy branch";

        var pills = branch.Standings.Select(s => new StandingPill(
            Text: s.State switch
            {
                Landed.Yes => $"✓ {s.Environment}",
                Landed.No => $"— {s.Environment}",
                _ => $"? {s.Environment}",
            },
            Tip: s.State switch
            {
                Landed.Yes => s.LandedAt is { } at
                    ? $"In {s.Target} since {at:yyyy-MM-dd HH:mm}"

                    // Not "fast-forwarded": no dated record is also what a merge
                    // older than the timeline window looks like, and stating a
                    // cause we did not establish is how this window would start
                    // being confidently wrong.
                    : $"In {s.Target}, with no merge commit found in the last year",
                Landed.No => $"Not in {s.Target} yet",
                _ => $"Couldn't check {s.Target} — the answer here is unknown, not no",
            },
            Back: Paint(s.State switch
            {
                Landed.Yes => Tone.ShippedTint,
                Landed.No => Tone.OpenTint,
                _ => Tone.UnsureTint,
            }),
            Ink: Paint(s.State switch
            {
                Landed.Yes => Tone.ShippedInk,
                Landed.No => Tone.OpenInk,
                _ => Tone.UnsureInk,
            }))).ToList();

        var side = branch.Side switch
        {
            RefSide.Local => "local only",
            RefSide.Remote => "remote only",
            _ => string.Empty,
        };

        return new BranchRow(
            Name: branch.Name,
            Sub: $"{Trim(branch.Subject, 90)} · {branch.Author} · {Friendly.Since(branch.Updated)}",
            Counts: counts,
            CountsTip: countsTip,
            Accent: Paint(accent),
            Back: Paint(branch.IsTarget ? Tone.DeployRow : Tone.Row),
            TargetLabel: branch.IsTarget && _snapshot?.Deployed.TryGetValue(branch.Name, out var env) == true
                ? env
                : string.Empty,
            TargetShown: branch.IsTarget ? Visibility.Visible : Visibility.Collapsed,
            SideLabel: branch.SafeToDelete ? "merged · safe to delete" : side,
            SideShown: branch.SafeToDelete || side.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            Pills: pills,
            Traced: new TraceMark(branch.Name == _tracing));
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_quiet) return;
        PaintBranches();
    }

    private void OnTraceBranch(object sender, RoutedEventArgs e)
    {
        if (Row<BranchRow>(sender) is not { } row) return;

        Trace(row.Name);

        // What is traced shows in the timeline, so the timeline has to be there.
        ShowTimeline(true);
    }

    private void OnClearTrace(object sender, RoutedEventArgs e) => Trace(null);

    /// <summary>
    /// Filter the timeline to one branch, or back to all of them. The rows are
    /// marked where they stand and not repainted, which would take the focus off
    /// the button that was just pressed.
    /// </summary>
    private void Trace(string? name)
    {
        _tracing = name;

        foreach (var row in BranchList.Items.OfType<BranchRow>())
            row.Traced.On = name is not null && row.Name == name;

        PaintTimeline();
    }

    // ---------- the timeline ----------

    private void PaintTimelineTargets()
    {
        if (_snapshot is not { Ok: true } snap || snap.Deployed.Count == 0)
        {
            TimelineTarget.ItemsSource = null;
            return;
        }

        var primary = _project?.PrimaryTarget;

        var targets = snap.Deployed.Keys
            .OrderByDescending(t => string.Equals(t, primary, StringComparison.Ordinal))
            .ToList();

        TimelineTarget.ItemsSource = targets;

        _timelineTarget ??= targets[0];
        TimelineTarget.SelectedItem = _timelineTarget;
    }

    private void OnTimelineTargetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TimelineTarget.SelectedItem is not string target || target == _timelineTarget) return;

        _timelineTarget = target;
        PaintTimeline();
    }

    private void PaintTimeline()
    {
        TracePanel.Visibility = _tracing is null ? Visibility.Collapsed : Visibility.Visible;
        TraceNote.Text = _tracing is null ? string.Empty : $"only {_tracing}";

        if (_snapshot is not { Ok: true } snap)
        {
            Timeline.ItemsSource = null;
            TimelineEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        var landings = snap.Merges
            .Where(m => _timelineTarget is null || m.Target == _timelineTarget)
            .Where(m => _tracing is null || m.Source == _tracing)
            .Take(150)
            .ToList();

        var rows = new List<LandingRow>(landings.Count);
        var lastDay = string.Empty;

        for (var i = 0; i < landings.Count; i++)
        {
            var landing = landings[i];
            var day = landing.At.ToString("ddd d MMM", CultureInfo.InvariantCulture);
            var newDay = day != lastDay;
            lastDay = day;

            rows.Add(new LandingRow(
                Day: day,
                DayShown: newDay ? Visibility.Visible : Visibility.Collapsed,
                Source: landing.Source,
                When: landing.At.ToString("HH:mm", CultureInfo.InvariantCulture),
                Detail: $"{landing.Author} · {Trim(landing.Subject, 110)}",
                Accent: Paint(landing.Source == _tracing ? Tone.Partly : Tone.Shipped),

                // Older entries recede slightly. Enough to give the list depth
                // as you scroll, never enough to make anything unreadable — the
                // floor is well above the point where text stops being legible.
                Fade: i < 8 ? 1.0 : Math.Max(0.72, 1.0 - (i - 8) * 0.01)));
        }

        Timeline.ItemsSource = rows;

        TimelineEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TimelineEmpty.Text = _tracing is not null
            ? $"No merge commit on {_timelineTarget} names {_tracing} in the last year. "
              + "A squash merge leaves no record of the branch it came from, so it would look like this too."
            : $"No merge commits on {_timelineTarget} in the last year.";
    }

    // ---------- layout ----------

    /// <summary>
    /// The repository pane narrows with the page, at the same width and to the
    /// same sizes as the lists pane on the Notes page. The first measure also
    /// decides whether the timeline starts open; after that it is the user's.
    /// </summary>
    private void OnPageSized(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= MainWindow.NotesListsWide;
        if (wide != _repoWide)
        {
            _repoWide = wide;
            RepoColumn.Width = new GridLength(wide ? 240 : 180);
        }

        // Not before the page has a width: it is never measured while hidden.
        if (_sized || e.NewSize.Width <= 0) return;

        _sized = true;
        ShowTimeline(e.NewSize.Width >= TimelineOnFrom);
    }

    /// <summary>
    /// Deployed and Diagrams side by side while the pane has room, one under the
    /// other when it has not, and the sort and filter follow them down. Only on
    /// crossing the line, so dragging the window edge across it is one re-layout.
    /// </summary>
    private void OnRightSized(object sender, SizeChangedEventArgs e)
    {
        var roomy = e.NewSize.Width >= SideBySideFrom;
        if (roomy == _roomy) return;

        _roomy = roomy;
        ArrangeBand();
        ArrangeToolbar();
    }

    private void ArrangeBand()
    {
        var deployed = DeployedStrip.Visibility == Visibility.Visible;
        var diagrams = DiagramStrip.Visibility == Visibility.Visible;
        var both = deployed && diagrams;
        var beside = both && _roomy;

        // With neither section there is nothing to put a band round.
        Band.Visibility = deployed || diagrams ? Visibility.Visible : Visibility.Collapsed;
        BandDivider.Visibility = both ? Visibility.Visible : Visibility.Collapsed;

        Grid.SetColumnSpan(DeployedStrip, beside ? 1 : 3);

        Grid.SetRow(BandDivider, beside ? 0 : 1);
        Grid.SetColumn(BandDivider, beside ? 1 : 0);
        Grid.SetColumnSpan(BandDivider, beside ? 1 : 3);
        BandDivider.Width = beside ? 1 : double.NaN;
        BandDivider.Height = beside ? double.NaN : 1;
        BandDivider.Margin = beside ? new Thickness(16, 0, 16, 8) : new Thickness(0, 4, 0, 12);

        Grid.SetRow(DiagramStrip, beside ? 0 : 2);
        Grid.SetColumn(DiagramStrip, beside ? 2 : 0);
        Grid.SetColumnSpan(DiagramStrip, beside ? 1 : 3);
        DiagramStrip.MaxWidth = beside ? 360 : 560;
    }

    private void ArrangeToolbar()
    {
        Grid.SetRow(ToolbarTools, _roomy ? 0 : 1);
        Grid.SetColumn(ToolbarTools, _roomy ? 1 : 0);
        Grid.SetColumnSpan(ToolbarTools, _roomy ? 1 : 2);
        ToolbarTools.HorizontalAlignment = _roomy ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
        ToolbarTools.Margin = _roomy ? new Thickness(0) : new Thickness(0, 8, 0, 0);
        BranchFilter.Width = _roomy ? 200 : 150;
        Grid.SetRow(SummaryChips, _roomy ? 1 : 2);
    }

    private void OnSplitSized(object sender, SizeChangedEventArgs e) => FitTimeline(e.NewSize.Width);

    /// <summary>
    /// A pixel column never gives way on its own, so a timeline opened on a narrow
    /// page — by the switch or by Trace — would run off the right edge. Cap it to
    /// what is left beside the branches, and let both shrink when even that is short.
    /// </summary>
    private void FitTimeline(double width)
    {
        if (width <= 0) return;

        var branches = Math.Min(BranchesFloor, width * 0.55);
        var room = Math.Max(0, width - branches - 6);

        BranchesColumn.MinWidth = branches;
        TimelineColumn.MaxWidth = room;
        TimelineColumn.MinWidth = _timelineOn ? Math.Min(260, room) : 0;
    }

    private void OnTimelineToggle(object sender, RoutedEventArgs e) =>
        ShowTimeline(TimelineToggle.IsChecked == true);

    /// <summary>
    /// The column goes to nothing rather than just being hidden, so the branches
    /// take its room; it comes back at the width it was dragged to.
    /// </summary>
    private void ShowTimeline(bool on)
    {
        TimelineToggle.IsChecked = on;
        if (on == _timelineOn) return;

        _timelineOn = on;

        if (on)
        {
            TimelineColumn.Width = _timelineWidth;
            SplitColumn.Width = new GridLength(6);
        }
        else
        {
            _timelineWidth = TimelineColumn.Width;
            TimelineColumn.MinWidth = 0;
            TimelineColumn.Width = new GridLength(0);
            SplitColumn.Width = new GridLength(0);
        }

        TimelinePane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (on) FitTimeline(BranchesColumn.ActualWidth + SplitColumn.ActualWidth + TimelineColumn.ActualWidth);
    }

    // ---------- plumbing ----------

    /// <summary>A question for the user, put to the main window's owner when there is one.</summary>
    private MessageBoxResult Ask(string text, string caption, MessageBoxButton buttons,
                                 MessageBoxImage image, MessageBoxResult otherwise) =>
        Window.GetWindow(this) is { } owner
            ? MessageBox.Show(owner, text, caption, buttons, image, otherwise)
            : MessageBox.Show(text, caption, buttons, image, otherwise);

    private static string Tail(string path)
    {
        var parts = path.TrimEnd('/', '\\').Split('/', '\\');
        return parts.Length <= 2 ? path : ".../" + string.Join('/', parts[^2..]);
    }

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static T? Row<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.DataContext as T;

    private static Brush Paint(string hex) => Palette.Of(hex);
}
