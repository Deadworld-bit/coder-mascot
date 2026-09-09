using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

// UseWindowsForms implicitly imports System.Drawing, which has its own Size.
using Size = System.Windows.Size;

namespace CoderMascot.UI;

/// <summary>
/// The speech bubble, as a separate always-on-top window that follows the
/// mascot. It never takes focus and never eats a click.
/// </summary>
public partial class BubbleWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly DispatcherTimer _hideTimer;

    public BubbleWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };

        Closed += (_, _) => _hideTimer.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);

        // Transparent: clicks fall through to whatever is underneath.
        // NoActivate: never steals focus from what you're typing in.
        // ToolWindow: stays out of Alt-Tab.
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        if (HideFromCapture)
        {
            try { SetWindowDisplayAffinity(hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */); }
            catch { /* pre-2004 Windows */ }
        }
    }

    /// <summary>Match the mascot's screen-capture setting. Set before showing.</summary>
    public bool HideFromCapture { get; set; } = true;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private Rect _lastMascot;
    private Rect _lastScreen;
    private bool _placed;

    /// <summary>Remember where to appear, even while hidden.</summary>
    public void Track(Rect mascot, Rect screen)
    {
        _lastMascot = mascot;
        _lastScreen = screen;
        _placed = true;
    }

    /// <summary>Say something. <paramref name="sticky"/> keeps it up until cleared.</summary>
    public void Say(string title, string body, bool sticky)
    {
        BubbleTitle.Text = title;
        BubbleBody.Text = body;

        // Force layout before reading ActualHeight, then place before showing.
        // Otherwise a three-line alert is positioned using the previous
        // message's height, and the very first bubble flashes at 0,0.
        Measure(new Size(Width, double.PositiveInfinity));
        if (_placed) Reposition(_lastMascot, _lastScreen);

        if (!IsVisible) Show();

        _hideTimer.Stop();
        if (!sticky) _hideTimer.Start();
    }

    public void Dismiss()
    {
        _hideTimer.Stop();
        if (IsVisible) Hide();
    }

    /// <summary>
    /// Park the bubble next to the mascot, kept fully on-screen. Prefers above,
    /// drops below when the mascot is at the top of the screen.
    /// </summary>
    public void FollowMascot(Rect mascot, Rect screen)
    {
        Track(mascot, screen);

        // The patrol timer fires 30x/sec whether or not there's anything to say;
        // repositioning a hidden window is pure waste.
        if (!IsVisible) return;

        Reposition(mascot, screen);
    }

    private void Reposition(Rect mascot, Rect screen)
    {
        // SizeToContent means ActualHeight is only valid once measured.
        var h = ActualHeight > 0 ? ActualHeight
              : DesiredSize.Height > 0 ? DesiredSize.Height
              : 64;
        const double gap = 6;

        var top = mascot.Top - h - gap;
        if (top < screen.Top) top = mascot.Bottom + gap;

        var left = mascot.Left + (mascot.Width - Width) / 2;

        Left = Math.Clamp(left, screen.Left, Math.Max(screen.Left, screen.Right - Width));
        Top = Math.Clamp(top, screen.Top, Math.Max(screen.Top, screen.Bottom - h));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
