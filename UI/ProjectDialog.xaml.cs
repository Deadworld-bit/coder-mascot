using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CoderMascot.Core;

using Brush = System.Windows.Media.Brush;

namespace CoderMascot.UI;

/// <summary>One deploy branch as it appears in the editor.</summary>
public sealed record TargetRow(
    DeployDraft Target, string Branch, string Hint, Visibility HintShown,
    Brush Accent, Visibility PrimaryShown, Visibility MakePrimaryShown);

/// <summary>
/// Adding and configuring a watched repository, without a text editor.
///
/// The thing this replaces was a sentence telling you to open config.json and
/// write a "projects" array by hand — which meant knowing the shape of the JSON,
/// typing an absolute path with no autocomplete, and typing branch names from
/// memory. All three fail quietly: a typo'd path reads as "doesn't exist", and a
/// typo'd branch name reads as "not merged" for a branch that shipped weeks ago.
///
/// So the form's real job is to know the two things it can find out for you.
/// It checks the folder is a repository before you save, and it offers the
/// branch names it found there, so the deploy branches are picked rather than
/// spelled. What it will not do is refuse to save when it couldn't check — a
/// stopped workspace or a laptop off the VPN is a bad reason to be unable to
/// write down a repository you already know you have.
/// </summary>
public partial class ProjectDialog : Window
{
    private readonly IBranchHost _host;
    private readonly ProjectDraft _draft;

    /// <summary>Other repositories already watched, by identity, so we can name a clash.</summary>
    private readonly IReadOnlyDictionary<string, string> _taken;

    private ProbeReport? _probe;

    /// <summary>The path+host the current probe belongs to, so a stale one is never shown.</summary>
    private string _probedFor = string.Empty;

    private CancellationTokenSource? _work;

    /// <summary>
    /// The look around for repositories, separately cancellable.
    ///
    /// Its own token rather than sharing the check's: over `coder ssh` this is a
    /// process with up to 45 seconds of patience, and closing the window has to
    /// end it — both so the connection goes away, and so nothing writes into a
    /// form that is no longer on screen.
    /// </summary>
    private CancellationTokenSource? _scan;

    /// <summary>A check is in flight. Said out loud, so typing can't overwrite it.</summary>
    private bool _checking;

    /// <summary>And that check is pulling a repository over the network, which is slower.</summary>
    private bool _downloading;

    /// <summary>The name box holds something the user typed, so stop guessing at it.</summary>
    private bool _nameIsTheirs;

    /// <summary>Setting a control from code, so its handler shouldn't read anything into it.</summary>
    private bool _quiet;

    /// <summary>The entry to save, or null when nothing is being saved.</summary>
    public ProjectEntry? Result { get; private set; }

    /// <summary>Stop watching the repository this was opened on.</summary>
    public bool Removed { get; private set; }

    /// <summary>Identity of the entry being replaced, or null for a new one.</summary>
    public string? Replacing => _draft.Replacing;

    public ProjectDialog(IBranchHost host, ProjectDraft draft, IReadOnlyDictionary<string, string> taken)
    {
        _host = host;
        _draft = draft;
        _taken = taken;

        // Before InitializeComponent: the radio button's IsChecked="True" fires
        // its Checked handler while the window is still being built.
        _quiet = true;
        InitializeComponent();

        HostRemote.IsChecked = draft.Host == RepoHost.Remote;
        HostWorkspace.IsChecked = draft.Host == RepoHost.Workspace;
        HostLocal.IsChecked = draft.Host == RepoHost.Local;
        PathBox.Text = draft.Path;
        NameBox.Text = draft.Name;

        // What is already in use, spelled the way it was first spelled. Offering
        // them is what stops "Backend" and "backend" becoming two headings that
        // look identical in the rail and behave as though they aren't.
        GroupBox.ItemsSource = ProjectGroups.Names(host.Configured.Select(p => p.Group));
        GroupBox.Text = draft.Group;

        // A name that came out of the config is the user's, even though they
        // typed it a month ago. Only a blank one is ours to fill in.
        _nameIsTheirs = draft.Name.Trim().Length > 0;

        EnvBox.ItemsSource = Environments.Common;

        if (!draft.IsNew)
        {
            HeadTitle.Text = draft.Name.Trim().Length > 0 ? draft.Name : "Repository";
            BtnRemove.Visibility = Visibility.Visible;
            BtnSave.Content = "Save changes";
        }

        _quiet = false;

        Closed += (_, _) =>
        {
            _work?.Cancel();
            _scan?.Cancel();
        };

        Loaded += (_, _) =>
        {
            PaintAll();

            if (_draft.Path.Trim().Length == 0)
            {
                PathBox.Focus();

                // Nothing typed yet, so the useful thing to have on screen is a
                // list of the repositories that are actually there.
                if (_draft.Host != RepoHost.Remote) Discover(quietly: true);
            }
            else if (!NeedsDownload)
            {
                Check();
            }
        };
    }

    // ---------- where it lives ----------

    private void OnHostChanged(object sender, RoutedEventArgs e)
    {
        if (_quiet) return;

        _draft.Host =
            HostLocal.IsChecked == true ? RepoHost.Local :
            HostRemote.IsChecked == true ? RepoHost.Remote :
            RepoHost.Workspace;

        // The same path means something different on the other side, so anything
        // learned about the old one is now a claim about a machine we didn't ask.
        Forget();
        HideFound();
        PaintAll();
    }

    // ---------- the folder ----------

    private void OnPathTyped(object sender, TextChangedEventArgs e)
    {
        if (_quiet) return;

        _draft.Path = PathBox.Text;
        SuggestName();

        // Typing invalidates a reading of a different path — but no new check is
        // started here: over `coder ssh` that would be one process per keystroke.
        if (!string.Equals(ProbeKey(), _probedFor, StringComparison.Ordinal)) Forget();

        PaintAll();
    }

    /// <summary>Fill the name in from the folder, until the user has an opinion.</summary>
    private void SuggestName()
    {
        if (_nameIsTheirs) return;

        _quiet = true;
        NameBox.Text = _draft.FolderName();
        _quiet = false;

        _draft.Name = NameBox.Text;
    }

    /// <summary>Leaving the box is the moment the path is finished being typed.</summary>
    private void OnPathLeft(object sender, RoutedEventArgs e)
    {
        if (_quiet || _draft.Path.Trim().Length == 0) return;
        if (string.Equals(ProbeKey(), _probedFor, StringComparison.Ordinal)) return;

        // Never for a URL with nothing downloaded yet: tabbing out of a box is
        // not consent to pull a repository over the network.
        if (NeedsDownload) return;

        Check();
    }

    private void OnCheck(object sender, RoutedEventArgs e) => Check();

    private void OnDownload(object sender, RoutedEventArgs e) => Check(fetch: true);

    private void OnFind(object sender, RoutedEventArgs e)
    {
        if (_draft.Host == RepoHost.Local)
        {
            Browse();
            return;
        }

        Discover(quietly: false);
    }

    /// <summary>
    /// This repository has a URL but no copy yet, so the check has to download
    /// one before it can answer anything.
    /// </summary>
    private bool NeedsDownload =>
        _draft.Host == RepoHost.Remote &&
        _draft.Problem is null &&
        GitUrl.Clean(_draft.Path) is { } url &&
        !MirrorStore.Fetched(url);

    private void Browse()
    {
        try
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Pick the repository folder",
                Multiselect = false,
            };

            var start = _draft.Path.Trim();
            if (start.Length > 0)
            {
                try { picker.InitialDirectory = start; }
                catch { /* a path that isn't one yet — start wherever Windows likes */ }
            }

            if (picker.ShowDialog(this) != true) return;

            Use(picker.FolderName);
        }
        catch (Exception ex)
        {
            // OpenFolderDialog is .NET 8+; if it ever isn't there, typing still works.
            CrashLog.Write("folder picker", ex);
            Report($"Couldn't open the folder picker \u2014 {ex.Message}. Type the path instead.", Mood.Bad);
        }
    }

    /// <summary>Fill the box from something that was picked, and check it.</summary>
    private void Use(string path)
    {
        _quiet = true;
        PathBox.Text = path;
        _quiet = false;

        _draft.Path = path;
        SuggestName();

        HideFound();
        Forget();
        PaintAll();
        Check();
    }

    private async void Discover(bool quietly)
    {
        var host = _draft.Host;

        _scan?.Cancel();
        _scan = new CancellationTokenSource();
        var token = _scan.Token;

        if (!quietly)
        {
            FoundNote.Text = "Looking\u2026";
            FoundList.ItemsSource = null;
            FoundPanel.Visibility = Visibility.Visible;
        }

        IReadOnlyList<string> found;
        try
        {
            found = await _host.DiscoverAsync(host, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            CrashLog.Write("discover repositories", ex);
            found = [];
        }

        // The host may have been switched while we were looking; a list of
        // workspace paths under "on this PC" would be actively misleading.
        if (token.IsCancellationRequested || host != _draft.Host) return;

        if (found.Count == 0)
        {
            if (quietly)
            {
                HideFound();
                return;
            }

            FoundPanel.Visibility = Visibility.Visible;
            FoundList.ItemsSource = null;

            // Never "there are none": not finding any is also what a stopped
            // workspace, a missing coder CLI and an unusual folder layout look
            // like from here.
            FoundNote.Text = host == RepoHost.Workspace
                ? "Nothing found under ~/workspace/projects or ~/workspace/share-projects. "
                  + "If the workspace is stopped, or your repositories live elsewhere, type the path."
                : "Nothing found in the usual folders. Use Browse\u2026 to point at it.";
            return;
        }

        FoundList.ItemsSource = found;
        FoundPanel.Visibility = Visibility.Visible;
        FoundNote.Text = found.Count == 1
            ? "1 repository found \u2014 click to use it"
            : $"{found.Count} repositories found \u2014 click one to use it";
    }

    private void OnPickFound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string path) Use(path);
    }

    private void OnHideFound(object sender, RoutedEventArgs e) => HideFound();

    private void HideFound()
    {
        FoundPanel.Visibility = Visibility.Collapsed;
        FoundList.ItemsSource = null;
    }

    // ---------- checking ----------

    /// <summary>Path and host together: either one changing makes a reading stale.</summary>
    private string ProbeKey() => $"{_draft.Host}:{_draft.Path.Trim()}";

    private void Forget()
    {
        _probe = null;
        _probedFor = string.Empty;
        BranchPick.ItemsSource = null;
        PaintStatus();
    }

    private async void Check(bool fetch = false)
    {
        // Nothing worth asking git — and PaintStatus is already saying why.
        if (_draft.Problem is not null)
        {
            PaintAll();
            return;
        }

        var key = ProbeKey();

        // Either the button that says so was pressed, or there is nothing on
        // disk to read and the check cannot answer anything without fetching.
        var download = fetch || NeedsDownload;

        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;

        _checking = true;
        _downloading = download;
        BtnCheck.IsEnabled = false;
        BtnDownload.IsEnabled = false;
        PaintStatus();

        ProbeReport report;
        try
        {
            // Before the fetch, not after: this is the thing that makes the
            // fetch possible, and doing it in the other order means the first
            // attempt always fails.
            // Only when the fields are the ones on screen: a token typed while
            // the URL was https, left behind after switching to ssh, is not a
            // sign-in anybody is asking for.
            if (download && UsesPassword() && SecretBox.Password.Length > 0 &&
                await SignIn(token) is { } refused)
            {
                _probe = ProbeReport.Failed(refused);
                _probedFor = key;
                return;
            }

            report = await _host.ProbeAsync(Snapshot(), download, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            CrashLog.Write("probe repository", ex);
            report = ProbeReport.Failed(ex.Message);
        }
        finally
        {
            // However this ended. Returning early with the button still disabled
            // strands the form on whichever note was last written into it — the
            // same defect the Branches window had, and it looks like a hang.
            // A cancelled check leaves both to whichever one superseded it.
            if (!token.IsCancellationRequested)
            {
                _checking = false;
                _downloading = false;
                BtnCheck.IsEnabled = true;
                BtnDownload.IsEnabled = true;
                BtnTest.IsEnabled = true;
                PaintAll();
            }
        }

        if (token.IsCancellationRequested || !string.Equals(key, ProbeKey(), StringComparison.Ordinal)) return;

        _probe = report;
        _probedFor = key;

        // Same as the test button: being told something failed and then having
        // to go looking for the button that says what is a small cruelty.
        if (!report.Ok) ShowDetails(true);

        if (report.Ok)
        {
            var text = BranchPick.Text;
            BranchPick.ItemsSource = report.Branches;
            BranchPick.Text = text;

            // A brand new repository with nothing configured: the branch it is
            // sitting on is the overwhelmingly likely deploy branch, and having
            // it pre-filled turns three fields into one press of Add.
            if (_draft.Deploys.Count == 0 && BranchPick.Text.Trim().Length == 0 &&
                report.CurrentBranch is { Length: > 0 } current)
            {
                BranchPick.Text = current;
                EnvBox.Text = Environments.Suggest(current);
            }
        }

        PaintAll();
    }

    /// <summary>
    /// Hand what was typed to Git's credential store. Null when it worked.
    ///
    /// The box is emptied on success. Keeping a token on screen after it has
    /// been stored somewhere better serves nobody, and this window can be left
    /// open on a desk for a long time.
    /// </summary>
    private async Task<string?> SignIn(CancellationToken ct)
    {
        SignInNote.Text = "Handing it to Git\u2026";

        string? refused;
        try
        {
            refused = await _host.SignInAsync(Snapshot(), UserBox.Text, SecretBox.Password, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            CrashLog.Write("store credential", ex);
            refused = ex.Message;
        }

        if (refused is not null)
        {
            SignInNote.Text = string.Empty;
            return refused;
        }

        SecretBox.Clear();
        SignInNote.Text = "Saved to Git's credential store on this PC. "
                          + "Your other git tools can use it too.";
        return null;
    }

    // ---------- testing, and saying what happened ----------

    /// <summary>
    /// Ask the server directly, and change nothing.
    ///
    /// Separate from the check on purpose. When a download fails there are four
    /// things it can mean — the URL, the network, the sign-in, the account's
    /// access — and until they are told apart the only available move is to
    /// change things at random and press Download again.
    /// </summary>
    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (_draft.Problem is not null)
        {
            PaintAll();
            return;
        }

        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;

        BtnTest.IsEnabled = false;
        Report("Asking the server\u2026", Mood.Working);

        RemoteTest result;
        try
        {
            // A token that has just been typed is part of the test: testing with
            // the old one and reporting a refusal would be answering a question
            // nobody asked.
            if (UsesPassword() && SecretBox.Password.Length > 0 && await SignIn(token) is { } refused)
            {
                Report(refused, Mood.Bad);
                return;
            }

            result = await _host.TestAsync(Snapshot(), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            CrashLog.Write("test remote", ex);
            result = RemoteTest.Failed(ex.Message, null);
        }
        finally
        {
            // Both, for the same reason the check restores both: these two share
            // a cancellation source, so one superseding the other must not leave
            // the loser's button disabled for the life of the window.
            if (!token.IsCancellationRequested)
            {
                BtnTest.IsEnabled = true;
                BtnCheck.IsEnabled = true;
            }
        }

        if (token.IsCancellationRequested) return;

        Report(result.Summary, result.Reachable ? Mood.Good : Mood.Bad);

        // A failure opens the details by itself. Somebody who has just been told
        // something went wrong should not also have to find the button that says
        // what — and on success it stays out of the way.
        if (!result.Reachable) ShowDetails(true);
    }

    private void OnDetails(object sender, RoutedEventArgs e) =>
        ShowDetails(DetailBox.Visibility != Visibility.Visible);

    private void ShowDetails(bool shown)
    {
        DetailBox.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        BtnSaveLog.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        BtnDetails.Content = shown ? "Hide what git said" : "Show what git said";

        if (shown) DetailBox.Text = GitLog.Report();
    }

    private void OnSaveLog(object sender, RoutedEventArgs e)
    {
        SignInNote.Text = GitLog.Save() is { } path
            ? $"Saved to {path}"
            : "Couldn't write the file. Select the text above and copy it instead.";
    }

    /// <summary>The draft as a project, for the one command that needs one.</summary>
    private GitProject Snapshot() => new()
    {
        Name = _draft.Name,
        Path = _draft.Path.Trim(),
        Host = _draft.Host,
    };

    // ---------- name ----------

    private void OnNameTyped(object sender, TextChangedEventArgs e)
    {
        if (_quiet) return;

        _nameIsTheirs = NameBox.Text.Trim().Length > 0;
        _draft.Name = NameBox.Text;
    }

    // ---------- group ----------

    /// <summary>
    /// Enter in the group box means "done typing", not "save the form".
    ///
    /// The dialog has a default button, so without this a new heading typed into
    /// an editable combo would submit the window on the keystroke that finished
    /// the word — before the box had committed the text it was holding.
    /// </summary>
    private void OnGroupKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        GroupBox.Focus();
    }

    // ---------- deploy branches ----------

    private void OnBranchKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // Enter in either box means "add this one", not "save the form" — the
        // default button would otherwise close the window mid-sentence.
        e.Handled = true;
        OnAddTarget(sender, e);
    }

    private void OnAddTarget(object sender, RoutedEventArgs e)
    {
        var branch = BranchPick.Text?.Trim() ?? string.Empty;

        if (branch.Length == 0)
        {
            AddNote.Text = _probe is { Ok: true }
                ? "Pick a branch from the list, or type one."
                : "Type the branch your CI deploys from.";
            BranchPick.Focus();
            return;
        }

        if (!_draft.Add(branch, EnvBox.Text ?? string.Empty))
        {
            AddNote.Text = $"{branch} is already in the list.";
            return;
        }

        _quiet = true;
        BranchPick.Text = string.Empty;
        BranchPick.SelectedItem = null;
        EnvBox.Text = string.Empty;
        EnvBox.SelectedItem = null;
        _quiet = false;

        AddNote.Text = string.Empty;
        PaintTargets();
        PaintSave();
        BranchPick.Focus();
    }

    private void OnRemoveTarget(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        _draft.Remove(row.Target);
        PaintTargets();
        PaintSave();
    }

    private void OnMakePrimary(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        _draft.MakePrimary(row.Target);
        PaintTargets();
    }

    private static TargetRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as TargetRow;

    // ---------- saving ----------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_draft.Problem is { } wrong)
        {
            Report(wrong, Mood.Bad);
            PathBox.Focus();
            return;
        }

        // Read at the last moment rather than on every keystroke: an editable
        // combo's Text and its SelectedItem disagree part-way through a pick, and
        // the only reading that matters is the one on screen when Save is pressed.
        _draft.Group = GroupBox.Text ?? string.Empty;

        var entry = _draft.ToEntry();
        var id = entry.ToProject().Id;

        // The same folder twice is one repository, and the list is deduplicated
        // on save — so without this the second copy's name and deploy branches
        // would be silently thrown away by the thing that keeps the list clean.
        if (_taken.TryGetValue(id, out var already))
        {
            Report($"That folder is already watched, as \u201c{already}\u201d. "
                   + "Edit that one instead, or point this at a different folder.", Mood.Bad);
            PathBox.Focus();
            return;
        }

        Result = entry;
        DialogResult = true;
        Close();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var name = _draft.Name.Trim().Length > 0 ? _draft.Name.Trim() : _draft.Path.Trim();

        var sure = MessageBox.Show(
            this,
            $"Stop watching {name}?\n\nOnly this app's list changes \u2014 the folder and the repository "
            + "on disk are left exactly as they are.",
            "Remove repository",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        if (sure != MessageBoxResult.OK) return;

        Removed = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ---------- painting ----------

    private void PaintAll()
    {
        PaintPath();
        PaintStatus();
        PaintTargets();
        PaintSave();
    }

    private void PaintPath()
    {
        var host = _draft.Host;

        HostNote.Text = host switch
        {
            RepoHost.Remote =>
                "Fetched once into a copy of its own \u2014 commits and history, no file contents, "
                + "no working tree. Every read after that is a local one, which is what makes this "
                + "the quick option.",
            RepoHost.Workspace =>
                "Read over `coder ssh`, using the workspace this app already watches. "
                + "Accurate, but every command is a round trip.",
            _ => "A clone you already have checked out on this machine.",
        };

        PathHead.Text = host == RepoHost.Remote ? "REPOSITORY URL" : "FOLDER";

        // ssh signs in with a key, and a folder needs no sign-in at all — two
        // boxes that cannot help are two boxes somebody will fill in anyway.
        SignInPanel.Visibility = host == RepoHost.Remote && UsesPassword()
            ? Visibility.Visible
            : Visibility.Collapsed;

        // There is no server to test for a folder, on this PC or in the workspace.
        BtnTest.Visibility = host == RepoHost.Remote ? Visibility.Visible : Visibility.Collapsed;

        // Always, for a URL repository. There is no state in which fetching one
        // is not an action you might want, and deciding for somebody which of
        // two actions they meant is how the one they wanted ends up nowhere.
        BtnDownload.Visibility = BtnTest.Visibility;

        // Nothing to browse for a URL, and nothing to guess at either.
        BtnFind.Visibility = host == RepoHost.Remote ? Visibility.Collapsed : Visibility.Visible;
        BtnFind.Content = host == RepoHost.Workspace ? "Find\u2026" : "Browse\u2026";
        BtnFind.ToolTip = host == RepoHost.Workspace
            ? "List the repositories in your workspace"
            : "Pick the folder on this PC";

        PathBox.ToolTip = null;

        if (_draft.Path.Trim().Length == 0)
        {
            PathNote.Text = host switch
            {
                RepoHost.Remote => "The address you would clone, e.g. "
                                   + "https://git.example.com/team/contract-ui.git",
                RepoHost.Workspace => "A path inside the workspace, like /home/coder/workspace/projects/my-app.",
                _ => "A folder on this machine, like C:\\src\\my-app.",
            };
            return;
        }

        // Where the copy is and what it cost, for a repository whose only other
        // description is a URL. The full path is in the tooltip rather than the
        // line, because it is long and nobody needs to read it twice.
        if (host == RepoHost.Remote && GitUrl.Clean(_draft.Path) is { } url && MirrorStore.Fetched(url))
        {
            var fetched = MirrorStore.FetchedAt(url);

            PathNote.Text = "Copy on this PC: " + MirrorStore.Size(MirrorStore.Bytes(url))
                + (fetched is { } at ? $" \u00b7 fetched {Friendly.Since(at)}" : string.Empty)
                + " \u00b7 refresh it with \u201cFetch from origin\u201d.";
            PathBox.ToolTip = MirrorStore.PathFor(url);
            return;
        }

        PathNote.Text = string.Empty;
    }

    /// <summary>An https URL, which is the kind git asks for a password for.</summary>
    private bool UsesPassword() =>
        GitUrl.Clean(_draft.Path) is { } url
        && url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private void PaintStatus()
    {
        if (_draft.Path.Trim().Length == 0)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // Said as soon as it is true, rather than saved up for the Save button.
        if (_draft.Problem is { } wrong)
        {
            Report(wrong, Mood.Bad);
            return;
        }

        if (_checking)
        {
            Report(_downloading
                ? NeedsDownload
                    ? "Downloading the repository\u2026 this happens once, and a large one takes a while."
                    : "Fetching from the server\u2026 it is updating the copy already here, not starting over."
                : _draft.Host switch
                {
                    RepoHost.Workspace => "Asking the workspace\u2026",
                    RepoHost.Remote => "Reading the downloaded copy\u2026",
                    _ => "Looking\u2026",
                }, Mood.Working);
            return;
        }

        // A download that was tried and failed says why. Without this the panel
        // reverts to the invitation to press Download — which reads as though
        // nothing had happened at all, and is how somebody presses it four more
        // times before looking anywhere else.
        if (_probe is { Ok: false } refused &&
            string.Equals(_probedFor, ProbeKey(), StringComparison.Ordinal))
        {
            Report(refused.Problem ?? "Couldn't read that repository.", Mood.Bad);
            return;
        }

        // Not an error, and not a check that failed: nothing has been fetched,
        // and fetching is a thing to be asked for rather than done quietly.
        if (NeedsDownload)
        {
            Report("Not downloaded yet. Press Download to fetch a copy \u2014 "
                   + "history only, so it is a fraction of the repository.", Mood.Idle);
            return;
        }

        if (_probe is not { } report)
        {
            // Not "looks fine": nothing has been checked, and saying nothing at
            // all here is what leaves someone certain a typo'd path was accepted.
            Report("Not checked yet.", Mood.Idle);
            return;
        }

        if (!report.Ok)
        {
            Report(report.Summary, Mood.Bad);
            return;
        }

        if (report.Branches.Count == 0)
        {
            Report(report.Summary + (_draft.Host == RepoHost.Remote
                    ? " Press Download to fetch it again \u2014 the server said there were branches, "
                      + "so the copy on this PC is the part that went wrong."
                    : string.Empty),
                Mood.Idle);
            return;
        }

        Report(report.Summary, Mood.Good);
    }

    private void PaintTargets()
    {
        var known = _probe is { Ok: true } report ? report.Branches : null;

        TargetList.ItemsSource = _draft.Deploys.Select((deploy, index) =>
        {
            var branch = deploy.Branch.Trim();

            // Three states, and the middle one is the point: with no successful
            // check there is no list to be absent from, so nothing is claimed.
            var missing = known is not null &&
                          !known.Contains(branch, StringComparer.Ordinal);

            return new TargetRow(
                Target: deploy,
                Branch: branch,
                Hint: missing
                    ? "Not a branch in this repository right now \u2014 kept anyway, in case it's coming."
                    : string.Empty,
                HintShown: missing ? Visibility.Visible : Visibility.Collapsed,
                Accent: Paint(missing ? Tone.Unsure : known is null ? Tone.Open : Tone.Shipped),
                PrimaryShown: index == 0 && _draft.Deploys.Count > 1
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                MakePrimaryShown: index == 0 || _draft.Deploys.Count < 2
                    ? Visibility.Collapsed
                    : Visibility.Visible);
        }).ToList();

        TargetsEmpty.Text = _draft.Advice ?? string.Empty;
        TargetsEmpty.Visibility = _draft.Advice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PaintSave()
    {
        // Blocked only by what the form itself can see is wrong — a blank path,
        // a Windows path claimed to be in the workspace. Never by a failed
        // check: a stopped workspace or a laptop off the VPN is a bad reason to
        // be unable to write down a repository you already know you have. The
        // status panel says what couldn't be confirmed, and saving anyway is
        // then a decision rather than a slip.
        BtnSave.IsEnabled = _draft.Problem is null;
        SaveNote.Text = _draft.Problem is null ? string.Empty : "Fix the folder to save";
    }

    private enum Mood { Idle, Working, Good, Bad }

    private void Report(string message, Mood mood)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = message;

        // The button says what pressing it will do, which for a repository with
        // no copy yet is not "check" — it is minutes of network.


        // Kept current while it is open, so it shows the command that just ran
        // rather than the state of the world when it was first unfolded.
        if (DetailBox.Visibility == Visibility.Visible) DetailBox.Text = GitLog.Report();

        StatusText.Foreground = Paint(mood switch
        {
            Mood.Good => Tone.ShippedInk,
            Mood.Bad => Tone.BadInk,
            Mood.Working => Tone.Ink,
            _ => Tone.Faint,
        });

        StatusStripe.Background = Paint(mood switch
        {
            Mood.Good => Tone.Shipped,
            Mood.Bad => Tone.Bad,
            Mood.Working => Tone.Pick,
            _ => Tone.Line,
        });
    }

    private static Brush Paint(string hex) => Palette.Of(hex);
}
