using System.Globalization;
using System.Windows;
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
                                Brush Back, Brush Edge, Brush Mark, Brush BadgeBack, Brush BadgeInk);

/// <summary>
/// A heading in the project rail, which is also the control that folds it.
///
/// The open/shut triangle is a drawn shape picked by visibility rather than a
/// character in a font, because the one control here that hides half the list is
/// the last one that should depend on a glyph being present.
/// </summary>
public sealed record GroupBar(string Key, string Title, string Count, Visibility CountShown, string Tip,
                              Visibility ShutShown, Visibility OpenShown,
                              string Badge, Visibility BadgeShown,
                              Brush Ink, Brush Mark, Brush BadgeBack, Brush BadgeInk);

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
                               IReadOnlyList<StandingPill> Pills);

public sealed record LandingRow(string Day, Visibility DayShown, string Source, string When,
                                string Detail, Brush Accent, double Fade);

/// <summary>
/// Where work has got to, across every repository.
///
/// The question this window exists for is one you cannot answer from a branch
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
public partial class BranchesWindow : Window
{
    private readonly IBranchHost _host;

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

    /// <summary>
    /// How the branches are grouped, in the order the groups appear.
    ///
    /// The order is the order of the question being asked: what is deployed,
    /// then what hasn't got there, then what half has, then what could not be
    /// established — and only after all of that, the things that are finished.
    /// A flat list sorted by date puts a branch that shipped in March directly
    /// above one that has never shipped at all, and reading it is arithmetic.
    /// </summary>
    private sealed record Bucket(BranchGroup Key, string Title, string Note, string Accent);

    /// <summary>
    /// The groups, in the order the question is asked — which is not the order
    /// of the enum, and is the only thing this list decides. What belongs in
    /// each is <see cref="BranchLine.Group"/>'s business.
    /// </summary>
    private static readonly Bucket[] Buckets =
    [
        new(BranchGroup.Deploy, "DEPLOY BRANCHES", "what everything else is measured against", Tone.Deploy),
        new(BranchGroup.Open, "NOT SHIPPED ANYWHERE", "hasn't reached any deploy branch", Tone.Open),
        new(BranchGroup.Partly, "PARTLY SHIPPED", "in some environments, not all", Tone.Partly),
        new(BranchGroup.Unknown, "COULDN'T CHECK", "unknown, which is not the same as no", Tone.Unsure),
        new(BranchGroup.Shipped, "SHIPPED EVERYWHERE", "", Tone.Shipped),
        new(BranchGroup.Tidy, "SAFE TO DELETE", "merged everywhere, and the remote is gone", Tone.Tidy),
    ];

    public BranchesWindow(IBranchHost host)
    {
        _host = host;
        InitializeComponent();

        Closed += (_, _) => _reading?.Cancel();
        Loaded += (_, _) =>
        {
            SortBox.SelectedIndex = 0;
            Start();
        };

        // The three things you do most, without reaching for the mouse. Preview,
        // so they work while the focus is inside the filter box.
        PreviewKeyDown += OnShortcut;
    }

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
            Busy(null);
            return;
        }

        var pick = projects.FirstOrDefault(p => p.Id == wanted) ?? projects[0];

        // Forced: the deploy branches may have just changed, and every standing
        // in the cached reading was measured against the old ones.
        Select(pick, force: true);
    }

    /// <summary>Show it, or bring it forward if it's already up.</summary>
    public void Summon()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ---------- projects ----------

    private void PaintProjects()
    {
        var rows = new List<object>();
        var bands = ProjectGroups.Arrange(_host.Projects, p => p.Group);
        var folded = _host.Collapsed.ToHashSet(StringComparer.Ordinal);

        foreach (var band in bands)
        {
            // Only a named band can be folded. The leftovers are the bottom of
            // the list, and a heading you can hide the whole list behind is a
            // way to end up with an empty rail and no idea why.
            var shut = band.Named && folded.Contains(band.Key);

            if (band.Named) rows.Add(Heading(band, shut));
            if (shut) continue;

            foreach (var project in band.Items) rows.Add(Line(project, indented: band.Named));
        }

        ProjectList.ItemsSource = rows;
    }

    private GroupBar Heading(ProjectBand<GitProject> band, bool shut)
    {
        var read = band.Items.Select(_host.Cached).ToList();
        var broken = read.Any(s => s is { Ok: false });
        var outstanding = read.Sum(s => s is { Ok: true } ? s.Outstanding : 0);
        var holds = _project is not null && band.Items.Any(p => p.Id == _project.Id);

        return new GroupBar(
            Key: band.Key,
            Title: band.Title,
            Count: band.Items.Count.ToString(CultureInfo.InvariantCulture),

            // Only while the group is shut. Open, you can see how many there are
            // by looking, and the number sat beside the outstanding badge as a
            // second unexplained figure that shifted its neighbour as it came
            // and went.
            CountShown: shut ? Visibility.Visible : Visibility.Collapsed,
            ShutShown: shut ? Visibility.Visible : Visibility.Collapsed,
            OpenShown: shut ? Visibility.Collapsed : Visibility.Visible,
            Tip: shut
                ? $"{band.Items.Count} hidden. Click to open this group."
                : "Click to fold this group away.",

            // The roll-up stays on the heading in both states. A group folded
            // shut on something that needs attention, with nothing to say so, is
            // the fold quietly costing you the thing the badge is for.
            Badge: broken ? "!" : outstanding.ToString(CultureInfo.InvariantCulture),
            BadgeShown: broken || outstanding > 0 ? Visibility.Visible : Visibility.Collapsed,
            Ink: Paint(shut ? Tone.Faint : Tone.Ink2),
            Mark: Paint(shut && holds ? Tone.Pick : Tone.Sunk),
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
            // rather than as one more row. Ungrouped rows keep the old inset, so
            // a rail with no groups in it looks exactly as it did.
            Indent: indented ? new Thickness(27, 10, 0, 10) : new Thickness(13, 10, 0, 10),
            Back: Paint(chosen ? Tone.Card : Tone.Rail),
            Edge: Paint(Tone.Hair),

            // Which row you are on, said with a mark at the edge as well as a
            // slightly lighter fill: on a dark theme, two greys four steps
            // apart is not a signal anybody can rely on.
            Mark: Paint(chosen ? Tone.Pick : Tone.Rail),
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

        // Whatever was read last time goes up immediately — a window that starts
        // blank for eight seconds while ssh connects reads as broken.
        _snapshot = _host.Cached(project);
        PaintProjects();
        PaintAll();

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

    private void OnAddProject(object sender, RoutedEventArgs e)
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

        Edit(draft);
    }

    private void OnEditProject(object sender, RoutedEventArgs e)
    {
        if (_project is not { } project) return;

        // Straight from the file, not rebuilt out of what this window happens to
        // be holding: anything in the entry the window never displays would
        // otherwise be dropped by the act of opening and saving the editor.
        var entry = _host.Configured.FirstOrDefault(x => x.ToProject().Id == project.Id);
        if (entry is null) return;

        Edit(ProjectDraft.From(entry));
    }

    private void Edit(ProjectDraft draft)
    {
        var taken = _host.Configured
            .Select(x => x.ToProject())
            .Where(p => !string.Equals(p.Id, draft.Replacing, StringComparison.Ordinal))
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var dialog = new ProjectDialog(_host, draft, taken) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var entries = _host.Configured.ToList();
        var at = draft.Replacing is null
            ? -1
            : entries.FindIndex(x => string.Equals(x.ToProject().Id, draft.Replacing, StringComparison.Ordinal));

        string? wanted = null;

        if (dialog.Removed)
        {
            if (at >= 0) entries.RemoveAt(at);
        }
        else if (dialog.Result is { } saved)
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
                ? "Add the ones you ship from, and this window can tell you what has reached them."
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

            // No deploy branches means no reference, and an empty band with a
            // heading over it is just a thing to wonder about.
            DeployedStrip.Visibility = Visibility.Collapsed;
            return;
        }

        DeployedStrip.Visibility = Visibility.Visible;

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
    /// In the order the question is asked: what is deployed, then what hasn't
    /// got there, then what half has, then what couldn't be established — and
    /// only after all of that, the things that are finished.
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

    /// <summary>Ctrl+F to the filter, Esc to clear it, F5 to read again.</summary>
    private void OnShortcut(object sender, System.Windows.Input.KeyEventArgs e)
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

        // Escape undoes the narrowing, in the order it was most likely applied —
        // and only closes the window once there is nothing left to widen.
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
            Pills: pills);
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_quiet) return;
        PaintBranches();
    }

    private void OnTraceBranch(object sender, RoutedEventArgs e)
    {
        if (Row<BranchRow>(sender) is not { } row) return;

        _tracing = row.Name;
        PaintTimeline();
    }

    private void OnClearTrace(object sender, RoutedEventArgs e)
    {
        _tracing = null;
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

    // ---------- plumbing ----------

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
