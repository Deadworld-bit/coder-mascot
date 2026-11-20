using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CoderMascot.Core;

using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace CoderMascot.UI;

/// <summary>One entry in a column list. A null name is "(none)": the role is left out.</summary>
public sealed record ColumnChoice(string? Name, string Type)
{
    public static readonly ColumnChoice None = new(null, "");

    public string Label => Name ?? "(none)";

    public override string ToString() => Label;
}

/// <summary>
/// Adding a log source, or changing or dropping one: a folder, a SQL Server table
/// or a MongoDB collection.
///
/// The folder rule is <see cref="LogSourceRules.Inspect"/>, the same one the config
/// loader applies, so the form never accepts a folder the loader would then throw
/// away. What it adds is the two things the loader cannot know: that the folder is
/// already in the list, and whether it exists yet.
///
/// A table's connection string lives in the PasswordBox and nowhere else: it is read
/// for a probe or for the save, sealed by the host, and the box is cleared when the
/// form closes. It is never put in a field, a label or a message, so the only way it
/// can leave is the host's own call.
/// </summary>
public partial class LogSourceForm : UserControl, IDrawerForm
{
    /// <summary>A probe that hasn't answered by now is not going to; the connect timeouts are shorter.</summary>
    private static readonly TimeSpan AskLimit = TimeSpan.FromSeconds(45);

    private readonly ILogHost _host;
    private readonly LogSourceEntry? _editing;

    /// <summary>
    /// Whether the folder exists is asked of the disk once typing pauses, and off
    /// this thread: a mapped drive that has gone holds the answer for seconds, and
    /// the box must keep taking keys meanwhile.
    /// </summary>
    private readonly DispatcherTimer _probe = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private string _kind = LogSourceKinds.Folder;

    /// <summary>The folder as the box spells it, normalised, when it would do; null when it would not.</summary>
    private string? _folder;

    /// <summary>Whether <see cref="_folder"/> exists. Null until the disk has answered.</summary>
    private bool? _exists;

    /// <summary>Bumped when the folder changes, so an answer about the old one is dropped.</summary>
    private int _generation;

    private bool _busy;
    private bool _closed;

    /// <summary>The markup has been read: the controls announce themselves while it is.</summary>
    private bool _ready;

    /// <summary>Setting a control from code, so its handler doesn't read into it.</summary>
    private bool _quiet;

    // ---------- a table ----------

    private CancellationTokenSource? _cts;

    /// <summary>Bumped for every question and every reason to stop caring about the last one.</summary>
    private int _round;

    private bool _asking;

    /// <summary>The connection answered, since the connection string or database last changed.</summary>
    private bool _tested;

    /// <summary>The table the last question was about, so leaving the box twice doesn't ask twice.</summary>
    private string? _askedTable;

    /// <summary>The table the column lists and the role boxes belong to.</summary>
    private string? _columnsTable;

    /// <summary>A table the connection answered for, and that wasn't there.</summary>
    private string? _missingTable;

    /// <summary>The role boxes hold a mapping worth keeping: the saved one, a guess, or the user's.</summary>
    private bool _seeded;

    private List<ColumnChoice> _choices = [ColumnChoice.None];

    /// <summary>How much is in the connection box, without holding what it is.</summary>
    private int _typed;

    /// <summary>"server / database" as the last good probe saw it, and for which database field.</summary>
    private string? _probedHost;
    private string? _probedDatabase;

    /// <summary>The folder that was saved, or null when the form removed one or finished with nothing.</summary>
    public string? Chosen { get; private set; }

    public string Heading { get; }

    public string? Subheading { get; }

    public double PreferredWidth => 520;

    public event EventHandler<bool>? Finished;

    public LogSourceForm(ILogHost host, LogSourceEntry? editing)
    {
        _host = host;
        _editing = editing;
        Heading = editing is null ? "Add a log source" : "Edit log source";

        _kind = editing is not null && LogSourceKinds.IsTable(editing.Kind) ? editing.Kind : LogSourceKinds.Folder;

        // The kind can't change under an open form that is editing, so that one's line is exact.
        Subheading = editing is null
            ? "A folder of log files, or a SQL Server or MongoDB log table. The mascot reads it from there."
            : IsTable ? "The mascot reads new rows from it every few seconds."
            : "Paste log files into it; the mascot reads them from there.";

        InitializeComponent();

        NameBox.MaxLength = LogSourceRules.MaxNameChars;
        FilesBox.MaxLength = 512;
        DbBox.MaxLength = 128;
        ConnBox.MaxLength = 8192; // what the config accepts
        FilesBox.Text = editing?.Files is { Length: > 0 } files ? files : LogSourceRules.DefaultFiles;

        _quiet = true;

        if (editing is null)
        {
            KindFolder.IsChecked = true;
        }
        else
        {
            KindPick.Visibility = Visibility.Collapsed;
            KindFixed.Visibility = Visibility.Visible;
            KindFixed.Text = KindName(_kind);

            NameBox.Text = editing.Name ?? string.Empty;
            BtnRemove.Visibility = Visibility.Visible;
            BtnSave.Content = "Save changes";

            if (IsTable) Seed(editing);
            else FolderBox.Text = editing.Path ?? editing.Id;
        }

        _quiet = false;

        _probe.Tick += OnProbe;
        _ready = true;

        ShowKind();
        Update();
    }

    public void FocusFirst()
    {
        // A new source starts at the folder. An existing one has nothing that wants
        // typing first, and the name is the one box with no side effects.
        if (_editing is null) FolderBox.Focus();
        else NameBox.Focus();
    }

    public void Abandon() => Close();

    private void Finish(bool done)
    {
        Close();
        Finished?.Invoke(this, done);
    }

    /// <summary>Stop everything that is running, and let go of the connection string.</summary>
    private void Close()
    {
        _closed = true;
        _probe.Stop();
        StopAsking();
        ConnBox.Clear();
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool IsTable => LogSourceKinds.IsTable(_kind);

    private string Noun => _kind == LogSourceKinds.Mongo ? "collection" : "table";

    private static string KindName(string kind) => kind switch
    {
        LogSourceKinds.SqlServer => "SQL Server table",
        LogSourceKinds.Mongo => "MongoDB collection",
        _ => "Folder",
    };

    /// <summary>What the config calls a table that has no name of its own.</summary>
    internal static string TableLabel(string table) => table[(table.LastIndexOf('.') + 1)..].Trim('[', ']');

    private void ShowKind()
    {
        var table = IsTable;
        var mongo = _kind == LogSourceKinds.Mongo;

        FolderPane.Visibility = FilesPane.Visibility = table ? Visibility.Collapsed : Visibility.Visible;
        ConnectPane.Visibility = ColumnsPane.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        DatabasePane.Visibility = mongo ? Visibility.Visible : Visibility.Collapsed;

        var what = table ? Noun : "folder";
        BtnRemove.ToolTip = table
            ? $"Stops reading this {what}. Nothing in the database is touched."
            : "Stops reading this folder. Nothing on disk is touched.";
        NameNote.Text = $"Optional. What it's called in the list. Taken from the {what} unless you change it.";

        if (!table) return;

        TableHead.Text = Noun.ToUpperInvariant();
        ColumnsHead.Text = mongo ? "FIELDS" : "COLUMNS";
        TableHelp.Text = mongo ? "The collection's name" : "schema.table, like dbo.Logs";
        ConnHelp.Text = $"Use a login that can only read this {Noun}. The string is encrypted for your Windows account and never shown again.";

        ConnHint.Text = HasSaved
            ? "Saved and encrypted — leave empty to keep it, or paste a new one."
            : "Paste the connection string";
        System.Windows.Automation.AutomationProperties.SetHelpText(ConnBox, ConnHint.Text);
        ConnHint.Visibility = _typed > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>An existing table source has a connection string to keep; one edited by hand may not.</summary>
    // Only a sealed one can be kept: a plain one left by a hand edit must be pasted again, so it gets sealed.
    private bool HasSaved => LogSecrets.IsSealed(_editing?.Connection);

    private void OnKindChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        var kind = ReferenceEquals(sender, KindSql) ? LogSourceKinds.SqlServer
            : ReferenceEquals(sender, KindMongo) ? LogSourceKinds.Mongo
            : LogSourceKinds.Folder;

        if (kind == _kind) return;

        _kind = kind;
        ForgetTable();
        FailureNote.Visibility = Visibility.Collapsed;
        ShowKind();
        Update();
    }

    private void Update()
    {
        if (!_ready) return;

        if (IsTable) CheckTable();
        else Check();
    }

    // ---------- checking a folder ----------

    private void Check()
    {
        if (IsTable) return;

        var check = LogSourceRules.Inspect(FolderBox.Text);
        var folder = check.Folder;
        var problem = check.Problem;

        // An empty box is where everyone starts; it is not worth a red line.
        var show = FolderBox.Text.Trim().Length > 0;

        if (_editing is null && _host.Sources.Count >= LogSourceRules.MaxSources)
        {
            folder = null;
            show = true;
            problem = $"{LogSourceRules.MaxSources} log folders are already being read. Remove one to add another.";
        }
        else if (folder is not null && _host.Sources.Any(s => !Same(s.Id, _editing?.Id) && Same(s.Id, folder)))
        {
            folder = null;
            problem = "That folder is already a log source.";
        }
        else if (folder is not null && OnNetworkDrive(folder))
        {
            // The same reason shares are refused: a drive that has gone holds every listing.
            folder = null;
            problem = "That's a network drive. Copy the files to a folder on this PC.";
        }

        if (!Same(folder, _folder))
        {
            _folder = folder;
            _exists = null;
            _generation++;

            _probe.Stop();
            if (folder is not null) _probe.Start();
        }

        if (folder is null)
        {
            Note(show ? problem : null, Tone.BadInk);
        }
        else if (_exists == false)
        {
            Note("This folder doesn't exist yet. It will be created.", Tone.WarnInk);
        }
        else if (Synced(folder))
        {
            Note("This folder syncs to OneDrive. Logs hold customers' details; a folder that stays on this PC is safer.", Tone.WarnInk);
        }
        else
        {
            Note(null, Tone.Faint);
        }

        Field.SetHint(NameBox, folder is null ? "The folder's name" : folder[(folder.LastIndexOf('\\') + 1)..]);
        BtnSave.IsEnabled = folder is not null && !_busy;
    }

    /// <summary>Asks Windows what the letter is mapped to, not the drive itself, so it can't hang.</summary>
    private static bool OnNetworkDrive(string folder)
    {
        try
        {
            return new DriveInfo(folder[..3]).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Synced(string folder) =>
        new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" }
            .Select(Environment.GetEnvironmentVariable)
            .OfType<string>()
            .Where(root => root.Length > 3)
            .Any(root => folder.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                         || Same(folder, root.TrimEnd('\\')));

    private void Note(string? text, string tone)
    {
        FolderNote.Text = text ?? string.Empty;
        FolderNote.Foreground = Palette.Of(tone);
        FolderNote.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnProbe(object? sender, EventArgs e)
    {
        _probe.Stop();
        if (_folder is not { } folder) return;

        var generation = _generation;
        var exists = await Task.Run(() => Directory.Exists(folder));

        if (_closed || generation != _generation) return;

        _exists = exists;
        Check();
    }

    private void OnFolderTyped(object sender, TextChangedEventArgs e)
    {
        FailureNote.Visibility = Visibility.Collapsed;
        Check();
    }

    /// <summary>Patterns that did not survive cleaning are said so, instead of vanishing on Save.</summary>
    private void OnFilesTyped(object sender, TextChangedEventArgs e)
    {
        const string rule = "File-name patterns, separated by ;";

        var typed = FilesBox.Text;
        var cleaned = LogSourceRules.CleanFiles(typed);
        var asTyped = string.Join(';', typed.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        FilesNote.Text = typed.Trim().Length > 0 && !string.Equals(asTyped, cleaned, StringComparison.Ordinal)
            ? $"{rule} — will read {cleaned}"
            : rule;
    }

    private const string Unsaved =
        "The list changed, but the settings file couldn't be written, so it won't be remembered after a restart. Try again.";

    private void Fail(string text)
    {
        FailureNote.Text = text;
        FailureNote.Visibility = Visibility.Visible;
    }

    // ---------- browsing ----------

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Pick the log folder",
                Multiselect = false,
            };

            // Only a folder the disk has already said is there: the dialog is no place to wait on a dead drive.
            if (_folder is { } start && _exists == true)
            {
                try { picker.InitialDirectory = start; }
                catch { /* start wherever Windows likes */ }
            }

            var owner = Window.GetWindow(this);
            if ((owner is null ? picker.ShowDialog() : picker.ShowDialog(owner)) != true) return;

            FolderBox.Text = picker.FolderName;
        }
        catch (Exception ex)
        {
            // OpenFolderDialog is .NET 8+; if it ever isn't there, typing still works.
            Fail($"Couldn't open the folder picker — {ex.Message}. Type the path instead.");
        }
    }

    // ---------- a table: what is typed ----------

    private string TableText => (TableBox.Text ?? string.Empty).Trim();

    private string? DatabaseText => _kind == LogSourceKinds.Mongo && DbBox.Text.Trim() is { Length: > 0 } db ? db : null;

    private IReadOnlyList<ComboBox> Roles => [RoleId, RoleTime, RoleLevel, RoleMessage, RoleException, RoleProperties, RoleLogger];

    private LogColumnMap ReadMap() => new()
    {
        Id = ColumnOf(RoleId),
        Time = ColumnOf(RoleTime),
        Level = ColumnOf(RoleLevel),
        Message = ColumnOf(RoleMessage),
        Exception = ColumnOf(RoleException),
        Properties = ColumnOf(RoleProperties),
        Logger = ColumnOf(RoleLogger),
    };

    private static string? ColumnOf(ComboBox box) => (box.SelectedItem as ColumnChoice)?.Name;

    /// <summary>Runs <paramref name="change"/> with the handlers off, and puts them back as they were.</summary>
    private void Quietly(Action change)
    {
        var was = _quiet;
        _quiet = true;

        try { change(); }
        finally { _quiet = was; }
    }

    private void ShowMap(LogColumnMap? map) => Quietly(() =>
    {
        Pick(RoleId, map?.Id);
        Pick(RoleTime, map?.Time);
        Pick(RoleLevel, map?.Level);
        Pick(RoleMessage, map?.Message);
        Pick(RoleException, map?.Exception);
        Pick(RoleProperties, map?.Properties);
        Pick(RoleLogger, map?.Logger);
    });

    private void Pick(ComboBox box, string? name) =>
        box.SelectedItem = _choices.FirstOrDefault(c => c.Name is not null && Same(c.Name, name)) ?? ColumnChoice.None;

    private void SetChoices(IEnumerable<ColumnChoice> choices)
    {
        _choices = [ColumnChoice.None, .. choices];

        Quietly(() =>
        {
            foreach (var box in Roles) box.ItemsSource = _choices;
        });
    }

    /// <summary>Editing: what the source was saved with, shown before anything has been asked of the database.</summary>
    private void Seed(LogSourceEntry editing)
    {
        DbBox.Text = editing.Database ?? string.Empty;
        TableBox.Text = editing.Table ?? string.Empty;
        UtcBox.IsChecked = editing.Utc == true;

        // Only the names, so a save that changes nothing about them keeps them.
        var map = editing.Columns;
        string?[] names = map is null ? [] : [map.Id, map.Time, map.Level, map.Message, map.Exception, map.Properties, map.Logger];

        SetChoices(names.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Select(n => new ColumnChoice(n, "")));
        ShowMap(map);

        _seeded = map is not null;
        _columnsTable = editing.Table?.Trim();
    }

    /// <summary>The kind changed: nothing learned about the old one applies.</summary>
    private void ForgetTable()
    {
        StopAsking();
        PaintAsking();

        _tested = false;
        _askedTable = null;

        Quietly(() =>
        {
            TableBox.ItemsSource = null;
            TableBox.Text = string.Empty;
        });

        SayTest(null, Tone.Faint);
        ForgetColumns();
    }

    private void ForgetColumns()
    {
        SetChoices([]);
        ShowMap(null);

        _seeded = false;
        _columnsTable = null;
        _missingTable = null;

        PreviewPane.Visibility = Visibility.Collapsed;
        PreviewList.ItemsSource = null;
    }

    private void OnConnTyped(object sender, RoutedEventArgs e)
    {
        if (_closed) return;

        using (var secure = ConnBox.SecurePassword) _typed = secure.Length;
        ConnHint.Visibility = _typed > 0 ? Visibility.Collapsed : Visibility.Visible;

        if (_ready) ConnectionChanged();
    }

    private void OnDbTyped(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _quiet || _closed) return;

        // Another database's fields say nothing about this one's.
        ConnectionChanged();
        ForgetColumns();
    }

    /// <summary>What was found out with the old connection says nothing about the new one.</summary>
    private void ConnectionChanged()
    {
        StopAsking();
        PaintAsking();

        _tested = false;
        _askedTable = null;
        SayTest(null, Tone.Faint);
        FailureNote.Visibility = Visibility.Collapsed;
        Update();
    }

    private void OnConnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // Enter after pasting means "try it", and the form's own default button is still greyed out.
        e.Handled = true;
        OnTest(sender, e);
    }

    private void OnTableTyped(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _quiet || _closed) return;

        FailureNote.Visibility = Visibility.Collapsed;
        Update();
    }

    private void OnTablePicked(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _quiet || TableBox.SelectedItem is not string table) return;

        Commit(table);
    }

    /// <summary>Enter in the table box means "that's the one", not "save the form".</summary>
    private void OnTableKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        Commit(TableText);
    }

    private void OnTableFocus(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) Commit(TableText);
    }

    private void OnRoleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _quiet) return;

        _seeded = true;
        Stale();
        Update();
    }

    private void OnUtcClicked(object sender, RoutedEventArgs e)
    {
        Stale();
        Update();
    }

    /// <summary>The preview was read with another mapping; it stays, dimmed, until it is refreshed.</summary>
    private void Stale() => PreviewFrame.Opacity = 0.5;

    // ---------- a table: asking the database ----------

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (!_asking) await AskHost(TableText);
    }

    private async void OnRefreshPreview(object sender, RoutedEventArgs e)
    {
        if (!_asking) await AskHost(TableText);
    }

    /// <summary>A table was picked or typed: read its columns, unless they are already here or it was just asked about.</summary>
    private async void Commit(string text)
    {
        text = text.Trim();

        if (_closed || !IsTable) return;

        if (text.Length == 0 || Same(text, _askedTable) || Same(text, _columnsTable))
        {
            Update();
            return;
        }

        await AskHost(text);
    }

    private void StopAsking()
    {
        _round++;

        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* it had finished */ }

        _cts = null;
        _asking = false;
    }

    private void PaintAsking()
    {
        BtnTest.Content = _asking ? "Connecting…" : "Test connection";
        BtnTest.IsEnabled = !_asking;
        BtnPreview.IsEnabled = !_asking;
    }

    private LogSourceEntry Draft(string table) => new()
    {
        Kind = _kind,
        Table = table.Length > 0 ? table : null,
        Database = DatabaseText,

        // The saved, sealed one: the host opens it only when nothing was typed.
        Connection = _editing?.Connection,

        // Without a mapping for this table the reader guesses, as the form is about to.
        Columns = table.Length > 0 && Same(table, _columnsTable) && _choices.Count > 1 ? ReadMap() : null,
        Utc = UtcBox.IsChecked == true ? true : null,
    };

    private async Task AskHost(string table)
    {
        if (_closed) return;

        StopAsking();
        var round = _round;

        _askedTable = table;
        _asking = true;
        SayTest(null, Tone.Faint);
        PaintAsking();
        Update();

        var draft = Draft(table);
        var typed = ConnBox.Password;
        var cts = new CancellationTokenSource(AskLimit);
        _cts = cts;

        // What the rows will be read with, to tell whether the preview is still true when it comes back.
        var askedMap = ReadMap();
        var askedUtc = UtcBox.IsChecked == true;
        var warning = typed.Length == 0 ? null
            : _kind == LogSourceKinds.Mongo ? MongoLogReader.Warning(typed) : SqlLogReader.Warning(typed);

        TableProbe? probe = null;
        var timedOut = false;

        try
        {
            // Off this thread: a mongodb+srv name is resolved before the first await.
            probe = await Task.Run(() => _host.ProbeAsync(draft, typed.Length > 0 ? typed : null, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = cts.IsCancellationRequested && round == _round && !_closed;
        }
        catch (Exception)
        {
            // Never the exception's own words: they can carry the connection string.
            probe = new TableProbe("Couldn't connect.", null, [], [], []);
        }
        finally
        {
            typed = string.Empty;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }

        if (_closed || round != _round) return;

        _asking = false;
        PaintAsking();

        if (probe is null)
        {
            if (timedOut) SayTest($"No answer within {(int)AskLimit.TotalSeconds} seconds.", Tone.BadInk);
            Update();
            return;
        }

        Apply(probe, table, draft.Database, warning);
        if (!askedMap.SameAs(ReadMap()) || askedUtc != (UtcBox.IsChecked == true)) Stale();
        Update();
    }

    private void Apply(TableProbe probe, string table, string? database, string? warning)
    {
        var lists = probe.Tables.Count > 0 || probe.Columns.Count > 0;
        var where = probe.Host is { Length: > 0 } host ? $"Connected to {host}." : "Connected.";

        // The label for a kept connection comes from here: the form never opens the saved one itself.
        if (lists || probe.Problem is null)
        {
            _probedHost = probe.Host;
            _probedDatabase = database;
        }

        if (lists)
        {
            _tested = true;
            if (warning is null) SayTest(where, Tone.ShippedInk);
            else SayTest($"{where} {warning}", Tone.WarnInk);
        }
        else if (probe.Problem is { } why)
        {
            SayTest(why, Tone.BadInk);
        }
        else
        {
            _tested = true;
            SayTest($"{where} No {Noun}s found.", Tone.WarnInk);
        }

        if (probe.Tables.Count > 0)
        {
            // Setting the list can blank the typed text of an editable box.
            var typedTable = TableBox.Text;

            Quietly(() =>
            {
                TableBox.ItemsSource = probe.Tables;
                TableBox.Text = typedTable;
            });
        }

        // The box may have moved on while the question was out; its own commit asks again.
        if (table.Length == 0 || !Same(TableText, table)) return;

        if (probe.Columns.Count > 0)
        {
            UseColumns(table, probe.Columns);
        }
        else if (lists)
        {
            ForgetColumns();
            _missingTable = table;
        }

        if (probe.Columns.Count > 0 || (lists && probe.Problem is not null)) ShowPreview(probe);
        else if (lists) PreviewPane.Visibility = Visibility.Collapsed;
    }

    /// <summary>The columns are known: keep the mapping that fits them, else the saved one, else the guess.</summary>
    private void UseColumns(string table, IReadOnlyList<TableColumn> columns)
    {
        var current = _seeded && Same(_columnsTable, table) ? ReadMap() : null;

        var map = new[] { current, _editing?.Columns }.FirstOrDefault(m => m is not null && Fits(m, columns))
                  ?? LogTableRows.Guess(columns);

        _columnsTable = table;
        _missingTable = null;

        SetChoices(columns.Select(c => new ColumnChoice(c.Name, c.Type)));
        ShowMap(map);
        _seeded = true;
    }

    private static bool Fits(LogColumnMap map, IReadOnlyList<TableColumn> columns) =>
        new[] { map.Id, map.Time, map.Level, map.Message, map.Exception, map.Properties, map.Logger }
            .All(name => name is null || columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)));

    private void ShowPreview(TableProbe probe)
    {
        PreviewPane.Visibility = Visibility.Visible;
        PreviewFrame.Opacity = 1;

        var rows = probe.Preview.Take(5).Select(entry => new LogRow(entry)).ToList();

        PreviewList.ItemsSource = rows;
        PreviewFrame.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The connection and the lists worked; it is the rows that didn't, so it is said by the rows.
        if (probe.Problem is { } why) SayPreview(why, Tone.BadInk);
        else if (rows.Count == 0) SayPreview("No rows to show yet.", Tone.Faint);
        else SayPreview(null, Tone.Faint);
    }

    private void SayTest(string? text, string tone)
    {
        TestNote.Text = text ?? string.Empty;
        TestNote.Foreground = Palette.Of(tone);
        TestNote.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SayPreview(string? text, string tone)
    {
        PreviewNote.Text = text ?? string.Empty;
        PreviewNote.Foreground = Palette.Of(tone);
        PreviewNote.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- a table: what saving would do ----------

    private void CheckTable()
    {
        var table = TableText;
        var map = ReadMap();
        var mapped = map.Id is not null || map.Time is not null;
        var known = table.Length > 0 && Same(_columnsTable, table);
        var full = _editing is null && _host.Sources.Count >= LogSourceRules.MaxSources;
        var parts = _kind == LogSourceKinds.Mongo ? "fields" : "columns";

        string? columns = null;
        var columnsTone = Tone.BadInk;

        if (_kind == LogSourceKinds.Mongo && DatabaseText is { } database && LogSourceRules.DatabaseProblem(database) is { } badName)
        {
            columns = badName;
            mapped = false;
        }
        else if (table.Length > 0 && !_asking)
        {
            if (Same(_missingTable, table))
            {
                columns = $"That isn't one of the {Noun}s this login can see. Pick one from the list.";
            }
            else if (!known || _choices.Count == 1)
            {
                columns = $"Test the connection to list this {Noun}'s {parts}.";
                columnsTone = Tone.Faint;
            }
            else if (!mapped)
            {
                columns = "Pick an id or a time column.";
            }
        }

        ColumnsNote.Text = columns ?? string.Empty;
        ColumnsNote.Foreground = Palette.Of(columnsTone);
        ColumnsNote.Visibility = columns is null ? Visibility.Collapsed : Visibility.Visible;

        var ready = (_typed > 0 || HasSaved) && known && mapped && !full;

        // An existing source whose connection and place are as saved has nothing new to test.
        var changed = _editing is null || _typed > 0 || !Same(table, _editing.Table)
                      || !string.Equals(DatabaseText, _editing.Database, StringComparison.Ordinal);

        string? save = null;
        var saveTone = Tone.WarnInk;

        if (full)
        {
            save = $"{LogSourceRules.MaxSources} log sources are already being read. Remove one to add another.";
            saveTone = Tone.BadInk;
        }
        else if (ready && changed && !_tested)
        {
            save = "Not tested — the page will say if it can't be read.";
        }

        SaveNote.Text = save ?? string.Empty;
        SaveNote.Foreground = Palette.Of(saveTone);
        SaveNote.Visibility = save is null ? Visibility.Collapsed : Visibility.Visible;

        Field.SetHint(NameBox, table.Length > 0 ? TableLabel(table) : $"The {Noun}'s name");
        BtnSave.IsEnabled = ready;
    }

    // ---------- saving ----------

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (IsTable)
        {
            SaveTable();
            return;
        }

        if (_busy || _folder is not { } folder) return;

        Busy(true);
        FailureNote.Visibility = Visibility.Collapsed;

        var failure = await Task.Run(() => MakeFolder(folder));
        if (_closed) return;

        if (failure is not null)
        {
            Busy(false);
            Fail(failure);
            return;
        }

        var name = NameBox.Text.Trim();

        // A name that was only ever the old folder's own name follows the folder.
        if (_editing is { Path: { } old } && !Same(old, folder) && Same(name, old[(old.LastIndexOf('\\') + 1)..]))
            name = string.Empty;

        // Left empty, the name and the patterns are filled in by the same sieve the config uses.
        var entry = new LogSourceEntry
        {
            Name = name.Length == 0 ? null : name,
            Kind = LogSourceKinds.Folder,
            Path = folder,
            Files = LogSourceRules.CleanFiles(FilesBox.Text),
        };

        // Replaced in place, so correcting a folder doesn't send it to the bottom of a list you ordered.
        var entries = _host.Sources.ToList();
        var at = _editing is { } editing ? entries.FindIndex(s => Same(s.Id, editing.Id)) : -1;

        if (at >= 0) entries[at] = entry;
        else entries.Add(entry);

        var kept = _host.SaveSources(entries);

        if (kept is null)
        {
            Busy(false);
            Fail(Unsaved);
            return;
        }

        if (!kept.Any(k => Same(k.Id, folder)))
        {
            Busy(false);
            Fail("That folder wasn't saved. Check the path and try again.");
            return;
        }

        Chosen = folder;
        Finish(true);
    }

    private void SaveTable()
    {
        FailureNote.Visibility = Visibility.Collapsed;
        if (!BtnSave.IsEnabled) return;

        var table = TableText;
        var database = DatabaseText;
        var draft = new LogSourceEntry { Kind = _kind, Database = database, Table = table };

        string connection;
        string? host;

        // Read here and sealed at once; nothing keeps the plain text past this block.
        var typed = ConnBox.Password;

        try
        {
            if (typed.Length > 0)
            {
                connection = _host.Seal(typed);
                host = _host.HostOf(draft, typed) ?? "server";
            }
            else if (_editing is { Connection: { } saved } && LogSecrets.IsSealed(saved))
            {
                connection = saved;
                host = _probedHost is { } probed && string.Equals(_probedDatabase, database, StringComparison.Ordinal)
                    ? probed
                    : _editing.Host;
            }
            else
            {
                Fail("Paste the connection string.");
                return;
            }
        }
        catch (Exception)
        {
            Fail("Couldn't encrypt the connection string for this Windows account.");
            return;
        }
        finally
        {
            typed = string.Empty;
        }

        var name = NameBox.Text.Trim();

        // A name that was only ever the old table's own name follows the table.
        if (_editing is { Table: { } old } && !Same(old, table) && Same(name, TableLabel(old)))
            name = string.Empty;

        var entry = new LogSourceEntry
        {
            Name = name.Length == 0 ? null : name,
            Kind = _kind,
            Connection = connection,
            Host = host,
            Database = database,
            Table = table,
            Columns = ReadMap(),
            Utc = UtcBox.IsChecked == true ? true : null,
        };

        if (_host.Sources.Any(s => !Same(s.Id, _editing?.Id) && Same(s.Id, entry.Id)))
        {
            Fail($"That {Noun} is already a log source.");
            return;
        }

        var entries = _host.Sources.ToList();
        var at = _editing is { } editing ? entries.FindIndex(s => Same(s.Id, editing.Id)) : -1;

        if (at >= 0) entries[at] = entry;
        else if (entries.Count >= LogSourceRules.MaxSources)
        {
            Fail($"{LogSourceRules.MaxSources} log sources are already being read. Remove one to add another.");
            return;
        }
        else entries.Add(entry);

        var kept = _host.SaveSources(entries);

        if (kept is null)
        {
            Fail(Unsaved);
            return;
        }

        if (kept.FirstOrDefault(k => Same(k.Id, entry.Id)) is not { } now)
        {
            Fail("That source wasn't saved. Check the details and try again.");
            return;
        }

        Chosen = now.Id;
        Finish(true);
    }

    /// <summary>Null when the folder is there, now or after this; otherwise why it can't be.</summary>
    private static string? MakeFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                   or NotSupportedException or ArgumentException)
        {
            return $"Couldn't create the folder — {ex.Message}";
        }
    }

    private void Busy(bool on)
    {
        _busy = on;
        BtnSave.IsEnabled = !on && _folder is not null;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(false);

    // ---------- removing ----------

    private string Called => _editing switch
    {
        null => string.Empty,
        { Name: { Length: > 0 } name } => name,
        { Table: { } table } when IsTable => TableLabel(table),
        _ => _editing.Id,
    };

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        SureText.Text = IsTable
            ? $"Stop reading {Called}? Only this list changes — the {Noun} and its {(_kind == LogSourceKinds.Mongo ? "documents" : "rows")} stay where they are."
            : $"Stop reading {Called}? Only this list changes — the folder and its files stay where they are.";
        Ask(true);
        BtnKeep.Focus();
    }

    private void OnKeep(object sender, RoutedEventArgs e)
    {
        Ask(false);
        BtnRemove.Focus();
    }

    private void OnSure(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } editing) return;

        var kept = _host.SaveSources([.. _host.Sources.Where(s => !Same(s.Id, editing.Id))]);

        if (kept is null)
        {
            Ask(false);
            Fail(Unsaved);
            return;
        }

        if (kept.Any(k => Same(k.Id, editing.Id)))
        {
            Ask(false);
            Fail($"That {(IsTable ? "source" : "folder")} wasn't removed. Try again.");
            return;
        }

        Chosen = null;
        Finish(true);
    }

    /// <summary>Swap the footer between the buttons and the question. Enter answers whichever is showing, and the safe one by default.</summary>
    private void Ask(bool asking)
    {
        FooterMain.Visibility = asking ? Visibility.Collapsed : Visibility.Visible;
        FooterSure.Visibility = asking ? Visibility.Visible : Visibility.Collapsed;
        BtnSave.IsDefault = !asking;
        BtnKeep.IsDefault = asking;
    }

    /// <summary>Escape while the question is up takes the question back; the drawer's own Escape is for the form.</summary>
    private void OnFormKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || FooterSure.Visibility != Visibility.Visible) return;

        OnKeep(sender, e);
        e.Handled = true;
    }
}
