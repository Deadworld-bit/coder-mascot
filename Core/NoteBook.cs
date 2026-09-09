using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoderMascot.Core;

/// <summary>One thing worth remembering.</summary>
public sealed class Note
{
    [JsonPropertyName("id")] public string Id { get; set; } = NewId();
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;

    /// <summary>Which list it belongs to. Never blank once the book has loaded.</summary>
    [JsonPropertyName("group")] public string Group { get; set; } = NoteBook.DefaultGroup;

    /// <summary>Which paper it is written on — see <see cref="NoteColour"/>.</summary>
    [JsonPropertyName("colour")] public string Colour { get; set; } = NoteColour.Default;

    /// <summary>On the desktop right now, as its own little window.</summary>
    [JsonPropertyName("stuck")] public bool Stuck { get; set; }

    /// <summary>Where it was last dropped and how big it was: left, top, width, height.</summary>
    [JsonPropertyName("bounds")] public double[]? Bounds { get; set; }

    /// <summary>Keep it at the top of its list, above everything unpinned.</summary>
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }

    /// <summary>Dealt with. Kept rather than deleted, and sunk to the bottom.</summary>
    [JsonPropertyName("done")] public bool Done { get; set; }

    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("updated")] public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;

    public static string NewId() => Guid.NewGuid().ToString("n");
}

/// <summary>The file's shape. Groups are stored, not derived — see <see cref="NoteBook"/>.</summary>
internal sealed class NoteFile
{
    [JsonPropertyName("groups")] public List<string>? Groups { get; set; }
    [JsonPropertyName("activeGroup")] public string? ActiveGroup { get; set; }
    [JsonPropertyName("notes")] public List<Note>? Notes { get; set; }
}

/// <summary>
/// The scratchpad: whatever you'd otherwise put on a sticky note round the
/// monitor — the port a service is on, what you were doing before the meeting,
/// the thing to pick up tomorrow.
///
/// Notes live in named lists, because one flat pile stops being a scratchpad at
/// about twenty entries: work and home, or one list per project, are different
/// piles that happen to share a window. A note is in exactly one list, which is
/// the difference between filing something and tagging it — filing takes one
/// decision, and this has to be faster than opening a text file or it will not
/// get used.
///
/// Written to disk, unlike <see cref="StateHistory"/>, because that is the whole
/// point: history is the last hour of a running process, a note is meant to
/// outlive the reboot. Its own file rather than a corner of config.json, so a
/// hand-edited config can never cost you your notes and the file stays something
/// you can read, grep or sync yourself.
///
/// Deliberately free of WPF and Windows types — this is where the rules live, so
/// it has to be testable where the app itself cannot even run.
/// </summary>
public sealed class NoteBook
{
    /// <summary>The list that exists before you have made any of your own.</summary>
    public const string DefaultGroup = "General";

    /// <summary>Long enough to name a project, short enough to stay a tab.</summary>
    public const int MaxGroupNameLength = 40;

    private readonly List<Note> _notes = [];
    private readonly List<string> _groups = [];
    private string? _activeGroup;

    public NoteBook(string? path = null)
    {
        Path_ = path ?? DefaultPath;
        _groups.Add(DefaultGroup);
    }

    /// <summary>Where the notes live. Beside config.json, never inside it.</summary>
    public static string DefaultPath => System.IO.Path.Combine(CoderConfig.Dir, "notes.json");

    public string Path_ { get; }

    /// <summary>Set when a save failed, so the UI can say so instead of pretending.</summary>
    public string? LastError { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static NoteBook Load(string? path = null)
    {
        var book = new NoteBook(path);
        book.Read();
        return book;
    }

    private void Read()
    {
        string raw;
        try
        {
            if (!File.Exists(Path_)) return;
            raw = File.ReadAllText(Path_);
            if (string.IsNullOrWhiteSpace(raw)) return;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return;
        }

        try
        {
            // Notes were once a bare array, before they had lists to live in.
            // Anyone upgrading has a file in that shape and must not lose it.
            using var doc = JsonDocument.Parse(raw);
            var file = doc.RootElement.ValueKind == JsonValueKind.Array
                ? new NoteFile { Notes = JsonSerializer.Deserialize<List<Note>>(raw) }
                : JsonSerializer.Deserialize<NoteFile>(raw);

            Adopt(file);
            return;
        }
        catch (JsonException)
        {
            // falls through to the rescue below
        }

        // A corrupt file is moved aside, never written over. Config can afford
        // to shrug and use defaults — it is all re-derivable — but notes are the
        // one thing here the user cannot get back, so the bytes are kept even
        // when they can't be parsed.
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(Path_, System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Path_) ?? ".", $"notes-unreadable-{stamp}.json"));
            LastError = "Notes file was unreadable; it was kept as notes-unreadable-*.json.";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>
    /// Take what was on disk and make it consistent.
    ///
    /// The file is meant to be hand-editable, so a note may name a list that
    /// isn't declared. That creates the list rather than moving the note: the
    /// name someone typed is the intent, and quietly filing their note somewhere
    /// else is how you make a person stop trusting the feature.
    /// </summary>
    private void Adopt(NoteFile? file)
    {
        if (file is null) return;

        _groups.Clear();
        foreach (var name in file.Groups ?? []) AddGroup(name);

        foreach (var note in file.Notes ?? [])
        {
            if (note is null) continue;

            if (string.IsNullOrWhiteSpace(note.Id)) note.Id = Note.NewId();
            note.Text ??= string.Empty;

            // EnsureGroup, not AddGroup + Existing: a name over the length cap is
            // stored truncated, and looking the original back up would find
            // nothing and leave the note filed under a list that doesn't exist —
            // invisible under every tab, present only in the totals.
            note.Group = EnsureGroup(note.Group);
            note.Colour = NoteColour.Normalise(note.Colour);
            if (Area.FromArray(note.Bounds) is null) note.Bounds = null;

            _notes.Add(note);
        }

        if (_groups.Count == 0) _groups.Add(DefaultGroup);

        // An empty note is a deleted note that the app closed before it could
        // sweep up. Reading one back as a blank row would look like a bug.
        Prune();

        ActiveGroup = file.ActiveGroup;
    }

    // ---------- lists ----------

    public IReadOnlyList<string> Groups => _groups;

    /// <summary>
    /// The list being looked at, or null for "all of them".
    ///
    /// Remembered across restarts: a scratchpad you have to re-navigate to every
    /// morning is one you stop opening.
    /// </summary>
    public string? ActiveGroup
    {
        get => _activeGroup;
        set => _activeGroup = value is null ? null : Existing(value);
    }

    /// <summary>Where a new note goes: the list on screen, or the first one.</summary>
    public string AddTarget => ActiveGroup ?? _groups[0];

    private string? Existing(string name) =>
        _groups.FirstOrDefault(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The stored list of this name, creating it if it isn't there yet. Returns
    /// the canonical spelling, which is what a note must be filed under.
    /// </summary>
    private string EnsureGroup(string? name)
    {
        var clean = Clean(name);
        if (clean is null) return _groups.Count > 0 ? _groups[0] : DefaultGroup;

        if (Existing(clean) is { } already) return already;

        _groups.Add(clean);
        return clean;
    }

    /// <summary>Create a list. Blank names and ones that already exist do nothing.</summary>
    public bool AddGroup(string name)
    {
        var clean = Clean(name);
        if (clean is null || Existing(clean) is not null) return false;

        _groups.Add(clean);
        return true;
    }

    public bool RenameGroup(string from, string to)
    {
        if (Existing(from) is not { } current) return false;

        var clean = Clean(to);
        if (clean is null) return false;

        // Renaming to a name that is already taken would silently merge two
        // lists — plausibly what was meant, but not something to guess at.
        if (Existing(clean) is { } clash && !string.Equals(clash, current, StringComparison.Ordinal))
            return false;

        _groups[_groups.IndexOf(current)] = clean;

        foreach (var note in _notes.Where(n => string.Equals(n.Group, current, StringComparison.Ordinal)))
            note.Group = clean;

        if (string.Equals(_activeGroup, current, StringComparison.Ordinal)) _activeGroup = clean;
        return true;
    }

    /// <summary>
    /// Delete a list. Its notes move to the first remaining one rather than
    /// going with it — deleting a heading should never be a way to delete a
    /// dozen notes you had forgotten were filed under it.
    /// </summary>
    public bool RemoveGroup(string name)
    {
        if (Existing(name) is not { } current) return false;

        // There is always somewhere to put a note.
        if (_groups.Count == 1) return false;

        _groups.Remove(current);
        var home = _groups[0];

        foreach (var note in _notes.Where(n => string.Equals(n.Group, current, StringComparison.Ordinal)))
            note.Group = home;

        if (string.Equals(_activeGroup, current, StringComparison.Ordinal)) _activeGroup = null;
        return true;
    }

    /// <summary>How many notes a list is holding, for the tab label.</summary>
    public int OpenCount(string? group = null) => Scope(group).Count(n => !n.Done);

    public int DoneCount(string? group = null) => Scope(group).Count(n => n.Done);

    public int Count(string? group = null) => Scope(group).Count();

    private static string? Clean(string? name)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) return null;

        return clean.Length > MaxGroupNameLength ? clean[..MaxGroupNameLength].Trim() : clean;
    }

    private IEnumerable<Note> Scope(string? group) =>
        group is null
            ? _notes
            : _notes.Where(n => string.Equals(n.Group, group, StringComparison.OrdinalIgnoreCase));

    // ---------- notes ----------

    /// <summary>
    /// Everything in one list — or in all of them — in the order it should be
    /// read: unfinished before finished, pinned before the rest, then most
    /// recently touched first.
    /// </summary>
    public IReadOnlyList<Note> All(string? group = null) =>
    [
        .. Scope(group)
            .OrderBy(n => n.Done)
            .ThenByDescending(n => n.Pinned)
            .ThenByDescending(n => n.Updated)
    ];

    /// <summary>The same list, filtered. Blank query means everything.</summary>
    public IReadOnlyList<Note> Search(string? query, string? group = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return All(group);

        var needle = query.Trim();
        return [.. All(group).Where(n => n.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))];
    }

    public Note? Find(string id) => _notes.FirstOrDefault(n => n.Id == id);

    /// <summary>Add a note to a list, or to the one on screen. Blank text adds nothing.</summary>
    public Note? Add(string text, string? group = null)
    {
        var body = (text ?? string.Empty).Trim();
        if (body.Length == 0) return null;

        var target = group is null ? AddTarget : Existing(group) ?? AddTarget;

        var note = new Note { Text = body, Group = target, Colour = NoteColour.Default };
        _notes.Add(note);
        return note;
    }

    /// <summary>
    /// An empty note, for a sticky the user is about to type into.
    ///
    /// <see cref="Add"/> refuses blank text on purpose — an empty row in a list
    /// is indistinguishable from a rendering bug. A blank sticky is different:
    /// it is a piece of paper with the caret already on it, and if nothing gets
    /// written on it <see cref="Prune"/> takes it away again.
    /// </summary>
    public Note Blank(string? group = null)
    {
        var note = new Note
        {
            Text = string.Empty,
            Group = group is null ? AddTarget : Existing(group) ?? AddTarget,
            Colour = NoteColour.Default,
        };

        _notes.Add(note);
        return note;
    }

    /// <summary>File a note under a different list.</summary>
    public bool MoveTo(string id, string group)
    {
        if (Existing(group) is not { } target) return false;
        if (Find(id) is not { } note || string.Equals(note.Group, target, StringComparison.Ordinal))
            return false;

        note.Group = target;
        note.Updated = DateTimeOffset.Now;
        return true;
    }

    /// <summary>
    /// Retype a note's text. Called on every keystroke while editing, so it
    /// stamps <c>Updated</c> but never reorders anything by itself — the list is
    /// rebuilt on deliberate actions only, or a note would jump out from under
    /// the cursor mid-word.
    /// </summary>
    public bool SetText(string id, string text)
    {
        if (Find(id) is not { } note) return false;
        if (note.Text == text) return false;

        note.Text = text ?? string.Empty;
        note.Updated = DateTimeOffset.Now;
        return true;
    }

    public bool SetPinned(string id, bool pinned) => Touch(id, n => n.Pinned = pinned);

    /// <summary>Notes currently on the desktop, oldest placement first.</summary>
    public IReadOnlyList<Note> OnDesktop() => [.. _notes.Where(n => n.Stuck)];

    /// <summary>
    /// Put a note on the desktop, or take it back into the list.
    ///
    /// Unlike an edit, none of the three below stamp Updated. Moving a note
    /// around the screen, recolouring it or picking it up is handling it, not
    /// writing it — and stamping would shuffle the list under the user every
    /// time they nudged a window.
    /// </summary>
    public bool SetStuck(string id, bool stuck) => Change(id, n => n.Stuck = stuck);

    public bool SetColour(string id, string colour) =>
        Change(id, n => n.Colour = NoteColour.Normalise(colour));

    public bool SetBounds(string id, Area bounds) => Change(id, n => n.Bounds = bounds.ToArray());

    public bool SetDone(string id, bool done) => Touch(id, n => n.Done = done);

    public bool Remove(string id) => _notes.RemoveAll(n => n.Id == id) > 0;

    /// <summary>
    /// Drop every finished note in one list, or in all of them. Scoped to what
    /// is on screen: a tidy-up that also empties lists you are not looking at is
    /// not a tidy-up.
    /// </summary>
    public int ClearDone(string? group = null)
    {
        var doomed = Scope(group).Where(n => n.Done).Select(n => n.Id).ToHashSet();
        return _notes.RemoveAll(n => doomed.Contains(n.Id));
    }

    /// <summary>
    /// Delete anything that was emptied out.
    ///
    /// An empty note draws a blank row you cannot tell from a rendering bug, so
    /// clearing the text is how you delete one — but only once you've moved on
    /// from it, never while the caret is still sitting in it.
    ///
    /// A note on the desktop is never swept up, however empty. A new sticky
    /// starts blank by design, and the dashboard prunes on every repaint: the
    /// two together would delete the piece of paper between the shortcut being
    /// pressed and the first character being typed, and everything typed
    /// afterwards would go nowhere.
    /// </summary>
    public int Prune() =>
        _notes.RemoveAll(n => !n.Stuck && string.IsNullOrWhiteSpace(n.Text));

    private bool Change(string id, Action<Note> change)
    {
        if (Find(id) is not { } note) return false;

        change(note);
        return true;
    }

    private bool Touch(string id, Action<Note> change)
    {
        if (Find(id) is not { } note) return false;

        change(note);
        note.Updated = DateTimeOffset.Now;
        return true;
    }

    /// <summary>
    /// Write the file. Through a temporary file and a replace, because a torn
    /// write here loses every note there is — the config file can be rebuilt
    /// from defaults, this cannot be rebuilt from anything.
    /// </summary>
    public bool Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path_);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var file = new NoteFile
            {
                Groups = [.. _groups],
                ActiveGroup = _activeGroup,
                Notes = _notes,
            };

            var tmp = Path_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
            File.Move(tmp, Path_, overwrite: true);

            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }
}
