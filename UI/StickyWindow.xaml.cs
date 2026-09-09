using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CoderMascot.Core;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace CoderMascot.UI;

/// <summary>
/// One note, stuck to the desktop.
///
/// The whole point is that there is nothing between you and the text: the window
/// *is* the note. No edit mode, no save button, no dialog — you type on the
/// paper and it is written down. Everything else is a header 26 pixels tall.
///
/// It writes straight into the shared <see cref="NoteBook"/> as you type, and
/// asks for a save on a short delay rather than per keystroke, so a note being
/// typed into is not a file being rewritten thirty times a second.
/// </summary>
public partial class StickyWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint WDA_NONE = 0x00;

    private readonly NoteBook _book;
    private readonly CoderConfig _cfg;
    private readonly DispatcherTimer _save;

    /// <summary>Suppresses the write-back while the box is being filled in from the note.</summary>
    private bool _loading;

    public string NoteId { get; }

    /// <summary>Save what's on the paper — text, colour, where it sits.</summary>
    public event EventHandler? Dirty;

    /// <summary>Someone asked for another note from this one's menu.</summary>
    public event EventHandler? NewNoteRequested;

    /// <summary>Off the desktop: back into the list, or gone entirely.</summary>
    public event EventHandler<bool>? Released;

    public StickyWindow(NoteBook book, CoderConfig cfg, Note note, bool activate)
    {
        _book = book;
        _cfg = cfg;
        NoteId = note.Id;

        InitializeComponent();

        // A note restored at startup must not steal the focus of whatever the
        // user is actually doing; one they just asked for must take it.
        ShowActivated = activate;

        BuildColourMenu();
        Display(note);

        _save = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            Dirty?.Invoke(this, EventArgs.Empty);
        };

        LocationChanged += (_, _) => Remember();
        SizeChanged += (_, _) => Remember();
        Closed += (_, _) => _save.Stop();
    }

    /// <summary>
    /// Paint the window from the note — also the "it was edited somewhere else"
    /// path. Not called Show: that is Window's, and an overload of it here would
    /// be a coin toss at every call site.
    /// </summary>
    public void Display(Note note)
    {
        _loading = true;
        try
        {
            if (Body.Text != note.Text) Body.Text = note.Text;
        }
        finally
        {
            _loading = false;
        }

        GroupLabel.Text = note.Group;
        Paint(NoteColour.Of(note.Colour));
    }

    private void Paint(NotePaper paper)
    {
        Paper.Background = Fill(paper.Paper);
        Header.Background = Fill(paper.Header);
        Swatch.Fill = Fill(paper.Paper);
        Body.Foreground = Fill(paper.Ink);
        Body.CaretBrush = Fill(paper.Ink);
        GroupLabel.Foreground = Fill(paper.Faint);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Tool window: notes belong on the desktop, not in Alt+Tab. A dozen of
        // them would otherwise bury the windows you are actually switching to.
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

        // Same rule the mascot follows, and it matters more here: a note is
        // whatever you wrote on it, and you did not write it for the meeting
        // you are sharing your screen with.
        try
        {
            SetWindowDisplayAffinity(hwnd,
                _cfg.HideFromScreenCapture ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows. Nothing to do but leave it visible.
        }
    }

    /// <summary>Put the caret on the paper, for a note that was just made.</summary>
    public void FocusBody()
    {
        Activate();
        Body.Focus();
        Body.CaretIndex = Body.Text.Length;
    }

    public Area Bounds => new(Left, Top, Width, Height);

    // ---------- editing ----------

    private void OnBodyChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        _book.SetText(NoteId, Body.Text);
        Queue();
    }

    private void Remember()
    {
        // WPF reports NaN for a window that hasn't been laid out yet; writing
        // that would lose the note's position rather than record it.
        if (!IsLoaded || double.IsNaN(Left) || double.IsNaN(Top)) return;

        _book.SetBounds(NoteId, Bounds);
        Queue();
    }

    private void Queue()
    {
        _save.Stop();
        _save.Start();
    }

    /// <summary>Write now — the window is going away and the timer never will.</summary>
    public void Flush()
    {
        if (!_save.IsEnabled) return;

        _save.Stop();
        Dirty?.Invoke(this, EventArgs.Empty);
    }

    // ---------- the header ----------

    private void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        // DragMove throws if the button was released between the event and here.
        try { DragMove(); } catch (InvalidOperationException) { }

        Remember();
    }

    private void OnCycleColour(object sender, RoutedEventArgs e) => Recolour(NoteColour.Next(Colour()));

    private void BuildColourMenu()
    {
        foreach (var paper in NoteColour.All)
        {
            var item = new MenuItem
            {
                Header = char.ToUpperInvariant(paper.Id[0]) + paper.Id[1..],
                Icon = new System.Windows.Shapes.Rectangle
                {
                    Width = 12,
                    Height = 12,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = Fill(paper.Paper),
                    Stroke = Fill("#55000000"),
                    StrokeThickness = 1,
                },
            };

            var id = paper.Id;
            item.Click += (_, _) => Recolour(id);
            MenuColours.Items.Add(item);
        }
    }

    private string Colour() => _book.Find(NoteId)?.Colour ?? NoteColour.Default;

    private void Recolour(string colour)
    {
        _book.SetColour(NoteId, colour);
        Paint(NoteColour.Of(colour));
        Queue();
    }

    private void OnNewNote(object sender, RoutedEventArgs e) =>
        NewNoteRequested?.Invoke(this, EventArgs.Empty);

    private void OnDone(object sender, RoutedEventArgs e)
    {
        // Finishing something is the other way a note leaves the desktop. It is
        // kept, ticked, in its list — a note you can no longer find is a note
        // you cannot check you actually did.
        _book.SetDone(NoteId, true);
        Released?.Invoke(this, false);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Released?.Invoke(this, false);

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var text = _book.Find(NoteId)?.Text ?? string.Empty;
        var preview = text.Length > 60 ? text[..60] + "…" : text;

        if (MessageBox.Show(this, $"Delete this note?\n\n{preview}", "Coder Mascot",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        Released?.Invoke(this, true);
    }

    private static readonly Dictionary<string, Brush> Brushes = [];

    private static Brush Fill(string hex)
    {
        if (Brushes.TryGetValue(hex, out var cached)) return cached;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        Brushes[hex] = brush;
        return brush;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
