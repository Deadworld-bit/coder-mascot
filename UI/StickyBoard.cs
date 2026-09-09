using System.Windows;
using System.Windows.Forms;
using CoderMascot.Core;

using Screen = System.Windows.Forms.Screen;

namespace CoderMascot.UI;

/// <summary>
/// Every note currently stuck to the desktop.
///
/// One window per note, keyed by note id, so a note can never be open twice —
/// two windows over one note would fight over its text and the last one saved
/// would win. Asking for a note that is already up brings it forward instead.
///
/// The board owns the save: the windows report that something changed, and the
/// file is written once here rather than by each of them.
/// </summary>
public sealed class StickyBoard : IDisposable
{
    private readonly CoderConfig _cfg;
    private readonly NoteBook _book;
    private readonly Dictionary<string, StickyWindow> _open = [];

    /// <summary>Shutting down: windows are closing but the notes stay stuck.</summary>
    private bool _closingAll;

    /// <summary>A note's text, colour or place changed — the dashboard is now stale.</summary>
    public event EventHandler? Changed;

    public StickyBoard(CoderConfig cfg, NoteBook book)
    {
        _cfg = cfg;
        _book = book;
    }

    public int Count => _open.Count;

    public bool IsOpen(string id) => _open.ContainsKey(id);

    /// <summary>Put back whatever was on the desktop when the app last closed.</summary>
    public void RestoreSaved()
    {
        foreach (var note in _book.OnDesktop()) Open(note.Id, activate: false);
    }

    /// <summary>Stick a note to the desktop, or bring the one already there forward.</summary>
    public StickyWindow? Open(string id, bool activate = true)
    {
        if (_open.TryGetValue(id, out var already))
        {
            if (activate) already.FocusBody();
            return already;
        }

        if (_book.Find(id) is not { } note) return null;

        var window = new StickyWindow(_book, _cfg, note, activate);
        _open[id] = window;

        window.Dirty += (_, _) => Save();
        window.NewNoteRequested += (_, _) => CreateAtCursor();
        window.Released += (_, deleted) => Release(id, deleted);
        window.Closed += (_, _) =>
        {
            _open.Remove(id);

            // Closed by Alt+F4 or by the shell rather than through our own
            // buttons: that reads as putting the note away, so it must not
            // reappear tomorrow. Shutdown is the exception — there the desktop
            // is meant to come back exactly as it was left.
            if (_closingAll) return;

            _book.SetStuck(id, false);
            Save();
        };

        Place(window, note);

        _book.SetStuck(id, true);
        window.Show();
        if (activate) window.FocusBody();

        Save();
        return window;
    }

    /// <summary>
    /// Take a note off the desktop. It stays in the list — the sticky window is
    /// a way of looking at a note, never the note itself, so closing one must
    /// not be able to lose anything.
    /// </summary>
    public void Close(string id)
    {
        if (!_open.TryGetValue(id, out var window)) return;

        window.Flush();
        _book.SetStuck(id, false);
        window.Close();

        Save();
    }

    private void Release(string id, bool deleted)
    {
        if (_open.TryGetValue(id, out var window))
        {
            window.Flush();
            window.Close();
        }

        if (deleted) _book.Remove(id);
        else _book.SetStuck(id, false);

        Save();
    }

    /// <summary>A fresh, empty note under the pointer, ready to be typed into.</summary>
    public Note? CreateAtCursor()
    {
        // Empty on purpose: the note exists the moment you ask for it, and what
        // you type goes straight onto it. Prompting for text first would be the
        // dialog this whole feature is trying not to be. NoteBook.Prune sweeps
        // up anything left blank.
        var note = _book.Blank();
        note.Bounds = StickyPlacement.Near(Cursor().X, Cursor().Y, WorkArea(), _open.Count).ToArray();

        var window = Open(note.Id);
        window?.FocusBody();
        return note;
    }

    /// <summary>Hide while something is full-screen — same rule as the mascot.</summary>
    public void SetVisible(bool visible)
    {
        foreach (var window in _open.Values)
            window.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Repaint an open note after it was edited somewhere else.</summary>
    public void Refresh(string id)
    {
        if (_open.TryGetValue(id, out var window) && _book.Find(id) is { } note) window.Display(note);
    }

    private void Place(StickyWindow window, Note note)
    {
        var wanted = Area.FromArray(note.Bounds)
                     ?? StickyPlacement.Near(Cursor().X, Cursor().Y, WorkArea(), _open.Count - 1);

        var at = StickyPlacement.Clamp(wanted, WorkArea());

        window.Left = at.Left;
        window.Top = at.Top;
        window.Width = at.Width;
        window.Height = at.Height;

        _book.SetBounds(note.Id, at);
    }

    /// <summary>
    /// The pointer, in the units WPF positions windows with.
    ///
    /// Screen and Cursor report physical pixels; Window.Left is DIPs. The ratio
    /// is taken from the primary screen, which is right everywhere except a
    /// mixed-DPI setup — the same known gap the patrol has.
    /// </summary>
    private static (double X, double Y) Cursor()
    {
        try
        {
            var p = System.Windows.Forms.Cursor.Position;
            var scale = Scale();
            return (p.X / scale, p.Y / scale);
        }
        catch
        {
            return (SystemParameters.WorkArea.Left + 80, SystemParameters.WorkArea.Top + 80);
        }
    }

    private static double Scale()
    {
        try
        {
            var physical = Screen.PrimaryScreen?.Bounds.Width ?? 0;
            var dip = SystemParameters.PrimaryScreenWidth;
            return physical > 0 && dip > 0 ? physical / dip : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>Everything a note may be dropped on, taskbars excluded.</summary>
    private static Area WorkArea()
    {
        try
        {
            var scale = Scale();
            double left = double.MaxValue, top = double.MaxValue;
            double right = double.MinValue, bottom = double.MinValue;

            foreach (var screen in Screen.AllScreens)
            {
                var w = screen.WorkingArea;
                left = Math.Min(left, w.Left / scale);
                top = Math.Min(top, w.Top / scale);
                right = Math.Max(right, w.Right / scale);
                bottom = Math.Max(bottom, w.Bottom / scale);
            }

            if (right > left && bottom > top) return new Area(left, top, right - left, bottom - top);
        }
        catch
        {
            // Enumerating displays can throw during a session switch.
        }

        var area = SystemParameters.WorkArea;
        return new Area(area.Left, area.Top, area.Width, area.Height);
    }

    private void Save()
    {
        _book.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shutting down. The windows close but the notes stay marked stuck, so the
    /// desktop comes back exactly as it was left next time the app starts.
    /// </summary>
    public void Dispose()
    {
        _closingAll = true;

        foreach (var window in _open.Values.ToList())
        {
            window.Flush();
            window.Close();
        }

        _open.Clear();
        _book.Save();
    }
}
