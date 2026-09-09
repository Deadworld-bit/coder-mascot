using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CoderMascot.Core;

// UseWindowsForms implicitly imports System.Drawing and System.Windows.Forms,
// both of which define their own Point and MouseEventArgs.
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace CoderMascot.UI;

public partial class MascotWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Win10 2004+: keep the window off screenshots and shared screens.</summary>
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint WDA_NONE = 0x00;

    private readonly CoderConfig _cfg;
    private readonly DispatcherTimer _frameTimer;
    private readonly Dictionary<string, SpriteAnimation> _animations = new();
    private SpriteAnimation? _playing;
    private int _frame;
    private IntPtr _hwnd;
    private MascotState _shown = MascotState.Unknown;
    private bool _animating = true;

    /// <summary>False when no sprite frames were compiled in.</summary>
    private readonly bool _hasSprites;

    public event EventHandler? CheckRequested;
    public event EventHandler<bool>? PatrolToggled;

    /// <summary>
    /// "Yes, I've seen it" — a plain left-click on the character, or the menu
    /// item that does the same thing for a click-through mascot.
    ///
    /// Poking the thing that is waving at you is the most obvious way to tell it
    /// you got the message, and it's the one gesture that needs no menu, no
    /// aiming and no reading.
    /// </summary>
    public event EventHandler? Acknowledged;

    /// <summary>The user picked a new noise level from this character's menu.</summary>
    public event EventHandler<AlertPolicy>? AlertPolicyChanged;

    /// <summary>Show me everything — the detail window.</summary>
    public event EventHandler? DashboardRequested;

    /// <summary>Write something down — the dashboard, with the note box focused.</summary>
    public event EventHandler? NoteRequested;

    /// <summary>Connect this app to a Coder deployment.</summary>
    public event EventHandler? SetupRequested;

    /// <summary>Where work has got to, across the watched repositories.</summary>
    public event EventHandler? BranchesRequested;

    /// <summary>
    /// True when the user is touching the mascot — hovering or dragging — and
    /// nothing should move it. A 96px character crossing its own width in about
    /// a second is otherwise impossible to right-click reliably.
    /// </summary>
    public event EventHandler<bool>? HoldChanged;

    /// <summary>False when no sprite frames were compiled in.</summary>
    public bool HasSprites => _hasSprites;

    public Character Character { get; }

    public MascotWindow(CoderConfig cfg, Character character)
    {
        _cfg = cfg;
        Character = character;
        InitializeComponent();
        ApplySize(cfg.MascotSize);

        foreach (var name in Pose.All(character.Style))
        {
            if (SpriteSet.Load(character.Id, name, character.Fps(name)) is { } anim)
                _animations[name] = anim;
        }

        // Resolve fallbacks last-to-first, so a chain (climb -> climbidle ->
        // hang) can borrow a link that was itself filled in a moment ago.
        foreach (var name in Pose.All(character.Style).Reverse())
        {
            if (!_animations.ContainsKey(name) &&
                _animations.TryGetValue(SpriteSet.Fallback(name), out var alt))
                _animations[name] = alt;
        }

        _hasSprites = _animations.Count > 0;
        Sprite.Visibility = _hasSprites ? Visibility.Visible : Visibility.Collapsed;

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render);
        _frameTimer.Tick += (_, _) => AdvanceFrame();

        // Seed a frame now — ApplyLook only runs on a state *change*, so without
        // this the window is blank from Show() until the first poll returns.
        if (_hasSprites) Play("idle");

        MenuPatrol.IsChecked = cfg.Patrol;
        MenuStartWithWindows.IsChecked = cfg.StartWithWindows && StartupRegistration.IsEnabled();

        // Moving the window under an open context menu leaves the menu stranded.
        if (ContextMenu is { } menu)
        {
            menu.Opened += (_, _) => PatrolToggled?.Invoke(this, false);
            menu.Closed += (_, _) => PatrolToggled?.Invoke(this, MenuPatrol.IsChecked);
        }

        Closed += (_, _) => _frameTimer.Stop();

        RestorePosition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // ToolWindow: stays out of Alt-Tab; it's an ornament, not a window.
        // NoActivate: never pulls focus out of whatever you're typing in —
        // otherwise it steals focus at every launch, and at every login once
        // "Start with Windows" is on.
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        ApplyCaptureExclusion();
    }

    /// <summary>
    /// Optionally hide the mascot from screenshots and screen shares. Default on:
    /// a mascot patrolling across a shared presentation is a worse surprise than
    /// not being able to screen-record it.
    /// </summary>
    private void ApplyCaptureExclusion()
    {
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            SetWindowDisplayAffinity(_hwnd,
                _cfg.HideFromScreenCapture ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        }
        catch (Exception ex)
        {
            // Pre-2004 Windows doesn't support EXCLUDEFROMCAPTURE. Not fatal.
            Debug.WriteLine($"[CoderMascot] display affinity unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Lay the window out for a given size. Everything is proportional to it so
    /// the mascot can be resized from config without touching the XAML — and so
    /// the patrol engine, which measures the window, follows automatically.
    /// </summary>
    private void ApplySize(double size)
    {
        Width = size;
        Height = size;
        Root.Width = size;
        Root.Height = size;

        var sprite = Math.Round(size * 0.8);
        Sprite.Width = sprite;
        Sprite.Height = sprite;
        Canvas.SetLeft(Sprite, (size - sprite) / 2);
        Canvas.SetTop(Sprite, (size - sprite) / 2);

        // Mirror about the sprite's own centre, so turning around doesn't also
        // shift the character sideways within the window.
        Flip.CenterX = sprite / 2;
        Flip.CenterY = sprite / 2;

        var badge = Math.Max(8, Math.Round(size * 0.17));
        Bulb.Width = badge;
        Bulb.Height = badge;
        Bulb.StrokeThickness = Math.Max(1, badge / 12);
        Canvas.SetLeft(Bulb, size - badge - 1);
        Canvas.SetTop(Bulb, 1);
    }

    // ---------- pose ----------

    /// <summary>
    /// Called by the patrol engine: which animation, and whether it's mirrored.
    ///
    /// Nothing rotates. Each edge has artwork drawn for it — the character walks
    /// on the floor, hangs under the ceiling and climbs the walls upright — so
    /// the only transform left is the mirror that turns it around.
    /// </summary>
    public void SetPose(string animation, bool mirrored)
    {
        Play(animation);
        Flip.ScaleX = mirrored ? -1 : 1;
    }

    /// <summary>Face front again, and go back to whatever the state alone implies.</summary>
    public void ResetPose()
    {
        Flip.ScaleX = 1;
        Play(SpriteSet.ForState(_shown, Character.Style));
    }

    /// <summary>
    /// Move without churning: the patrol timer fires 30x/sec and spends most of
    /// it idling at an unchanged position, and each Left/Top assignment on an
    /// AllowsTransparency window costs a DWM recomposite.
    /// </summary>
    public void MoveTo(double left, double top)
    {
        if (Math.Abs(Left - left) < 0.5 && Math.Abs(Top - top) < 0.5) return;
        Left = left;
        Top = top;
    }

    private void Play(string name)
    {
        if (!_hasSprites) return;

        if (!_animations.TryGetValue(name, out var anim) &&
            !_animations.TryGetValue(SpriteSet.Fallback(name), out anim))
        {
            anim = _animations.Values.FirstOrDefault();
            if (anim is null) return;
        }

        if (ReferenceEquals(anim, _playing)) return;

        _playing = anim;
        _frame = 0;
        _frameTimer.Stop();
        _frameTimer.Interval = anim.FrameInterval;
        Sprite.Source = anim.Frames[0];

        if (_animating) _frameTimer.Start();
    }

    private void AdvanceFrame()
    {
        if (_playing is null) return;
        _frame = (_frame + 1) % _playing.Frames.Count;
        Sprite.Source = _playing.Frames[_frame];
    }

    // ---------- state rendering ----------

    /// <summary>Paint the mascot for a new reading.</summary>
    public void Render(WorkspaceSnapshot snap)
    {
        var state = snap.State;
        if (state == _shown) return;

        _shown = state;

        // The same table the dashboard's pip reads. Two copies of it drifted
        // apart the moment one was restyled, and the mascot is the half nobody
        // is looking at when they compare.
        Bulb.Fill = Brush(Tone.For(state));

        var shake = state is MascotState.Unreachable or MascotState.AgentLost or MascotState.WorkspaceDown;
        var pulse = state != MascotState.Connected && state != MascotState.Unknown;

        if (shake && _animating) Begin("ShakeStoryboard"); else Stop("ShakeStoryboard");
        if (pulse && _animating) Begin("PulseStoryboard"); else { Stop("PulseStoryboard"); Bulb.Opacity = 1; }

        // With patrol off nothing else drives the animation, so pick one here.
        if (!_cfg.Patrol) Play(SpriteSet.ForState(state, Character.Style));

        // MenuItem.Header treats "_" as an access-key marker, and Coder
        // workspace names routinely contain underscores.
        var label = (snap.WorkspaceName ?? "workspace").Replace("_", "__");
        MenuStatus.Header = $"{state.Title()} — {label}";
    }

    private static SolidColorBrush Brush(string hex) =>
        new((System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString(hex)!);

    private void Begin(string key) =>
        ((Storyboard)FindResource(key)).Begin(this, isControllable: true);

    private void Stop(string key) =>
        ((Storyboard)FindResource(key)).Stop(this);

    /// <summary>
    /// Pause every looping animation while the mascot is hidden. The window uses
    /// AllowsTransparency, which forces software rendering, so a forever
    /// animation behind a hidden window is pure wasted CPU.
    /// </summary>
    public void SetAnimating(bool on)
    {
        _animating = on;

        if (on)
        {
            if (_playing is not null) _frameTimer.Start();
        }
        else
        {
            _frameTimer.Stop();
            Stop("ShakeStoryboard");
            Stop("PulseStoryboard");
        }
    }

    // ---------- window behaviour ----------

    // Hovering parks the mascot so it can actually be clicked.
    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        HoldChanged?.Invoke(this, true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_pressed) HoldChanged?.Invoke(this, false);
    }

    private bool _pressed;
    private bool _dragged;
    private Point _pressAt;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount != 1) return;

        // Don't start a drag here. Calling DragMove on button-down means every
        // click is a drag: the first click of a double-click jitters the mascot,
        // and every stray click rewrites config.json.
        _pressed = true;
        _dragged = false;
        _pressAt = e.GetPosition(this);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || _dragged || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _pressAt.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _pressAt.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragged = true;

        try
        {
            DragMove();                      // blocks until the button is released
            SavePosition();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws if the button was already up; harmless.
        }
        finally
        {
            // _dragged deliberately stays set. DragMove swallows the release, so
            // whether a mouse-up arrives afterwards is up to Windows — and if it
            // does, moving the mascot must not also read as clicking it.
            _pressed = false;
            if (!IsMouseOver) HoldChanged?.Invoke(this, false);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        var clicked = _pressed && !_dragged;
        _pressed = false;
        if (!IsMouseOver) HoldChanged?.Invoke(this, false);

        // A click is "yes, I've seen it". No disambiguation timer, even though
        // this also fires as the first half of a double-click: the second half
        // opens the dashboard, which is itself an "I'm dealing with it", so
        // acknowledging on the way there is what you wanted anyway.
        if (clicked) Acknowledged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        // Was the Coder web dashboard. The local one answers more of the
        // questions you double-click a status light to ask — which session is
        // stuck, what's still running — and the web one is one button inside it.
        DashboardRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RestorePosition()
    {
        var area = SystemParameters.WorkArea;
        var saved = _cfg.MascotPositions.TryGetValue(Character.Id, out var p) && p.Length == 2
            ? p
            : null;

        // Stagger the default spot per character, so two mascots starting fresh
        // don't launch stacked on top of each other.
        var slot = Math.Max(0, Array.IndexOf(_cfg.Characters, Character.Id));
        var left = saved?[0] ?? (area.Right - Width - 24 - slot * (Width + 16));
        var top = saved?[1] ?? (area.Bottom - Height - 12);

        // Clamp to the whole virtual desktop: a monitor may have been unplugged,
        // but a second monitor is also a perfectly valid place to have left it.
        var minX = SystemParameters.VirtualScreenLeft;
        var maxX = minX + SystemParameters.VirtualScreenWidth - Width;
        var minY = SystemParameters.VirtualScreenTop;
        var maxY = minY + SystemParameters.VirtualScreenHeight - Height;

        Left = Math.Clamp(left, minX, Math.Max(minX, maxX));
        Top = Math.Clamp(top, minY, Math.Max(minY, maxY));
    }

    private void SavePosition()
    {
        if (_cfg.MascotPositions.TryGetValue(Character.Id, out var p) &&
            p.Length == 2 && p[0] == Left && p[1] == Top)
            return;

        _cfg.MascotPositions[Character.Id] = [Left, Top];
        _cfg.Save();
    }

    // ---------- menu ----------

    private void OnCheckNow(object sender, RoutedEventArgs e) =>
        CheckRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Show or hide the "not set up" line.
    ///
    /// The session watch needs a workspace-side install, and until that has
    /// happened it reports nothing at all — which is indistinguishable from
    /// reporting that all is well. A watchdog is allowed to be quiet; it is not
    /// allowed to be quiet because nobody plugged it in.
    /// </summary>
    public void SetSessionWatchMissing(bool missing) =>
        MenuSessionWatch.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;

    private void OnSessionWatchHelp(object sender, RoutedEventArgs e)
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

    private void OnShowDashboard(object sender, RoutedEventArgs e) =>
        DashboardRequested?.Invoke(this, EventArgs.Empty);

    private void OnNewNote(object sender, RoutedEventArgs e) =>
        NoteRequested?.Invoke(this, EventArgs.Empty);

    private void OnShowSetup(object sender, RoutedEventArgs e) =>
        SetupRequested?.Invoke(this, EventArgs.Empty);

    private void OnShowBranches(object sender, RoutedEventArgs e) =>
        BranchesRequested?.Invoke(this, EventArgs.Empty);

    private void OnDismiss(object sender, RoutedEventArgs e) =>
        Acknowledged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Grey out "I know about this" when there is nothing to know about.
    ///
    /// An always-live dismiss button is a button that mostly does nothing, and
    /// one that does nothing is one you stop believing in.
    /// </summary>
    public void SetDismissable(bool on) => MenuDismiss.IsEnabled = on;

    private void OnAlertsAll(object sender, RoutedEventArgs e) => Choose(AlertPolicy.All);
    private void OnAlertsQuiet(object sender, RoutedEventArgs e) => Choose(AlertPolicy.Quiet);
    private void OnAlertsOff(object sender, RoutedEventArgs e) => Choose(AlertPolicy.Off);

    private void Choose(AlertPolicy policy)
    {
        SetAlertPolicy(policy);
        AlertPolicyChanged?.Invoke(this, policy);
    }

    /// <summary>
    /// Show which noise level is in force. Three checkable items behaving as one
    /// radio group: clicking any of them re-checks all three from the winner, so
    /// clicking the current one can't leave the menu with nothing ticked.
    /// </summary>
    public void SetAlertPolicy(AlertPolicy policy)
    {
        MenuAlertsAll.IsChecked = policy == AlertPolicy.All;
        MenuAlertsQuiet.IsChecked = policy == AlertPolicy.Quiet;
        MenuAlertsOff.IsChecked = policy == AlertPolicy.Off;
    }

    private void OnTogglePatrol(object sender, RoutedEventArgs e)
    {
        _cfg.Patrol = MenuPatrol.IsChecked;
        _cfg.Save();
        PatrolToggled?.Invoke(this, MenuPatrol.IsChecked);
    }

    private void OnOpenDashboard(object sender, RoutedEventArgs e)
    {
        // UseShellExecute means "ask the shell what this string means", not
        // "open a URL" — a config value of \\host\share\payload.exe would be
        // launched. Only ever hand it a parsed http(s) URI.
        if (!Uri.TryCreate(_cfg.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;

        Shell(uri.AbsoluteUri);
    }

    private void OnEditConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CoderConfig.Dir);
            if (!File.Exists(CoderConfig.Path_)) _cfg.Save();
            Shell(CoderConfig.Path_);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open config: {ex.Message}",
                "Coder Mascot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnToggleClickThrough(object sender, RoutedEventArgs e)
    {
        SetClickThrough(MenuClickThrough.IsChecked);

        if (MenuClickThrough.IsChecked)
        {
            // Once clicks pass through, the only way back is the tray menu.
            MessageBox.Show(
                "The mascot now ignores the mouse. Turn this off from the tray icon menu.",
                "Coder Mascot", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public void SetClickThrough(bool on)
    {
        MenuClickThrough.IsChecked = on;
        if (_hwnd == IntPtr.Zero) return;

        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
    }

    public bool IsClickThrough => MenuClickThrough.IsChecked;

    private void OnToggleStartup(object sender, RoutedEventArgs e)
    {
        var enable = MenuStartWithWindows.IsChecked;

        if (StartupRegistration.Set(enable) is { } problem)
        {
            MenuStartWithWindows.IsChecked = !enable;
            MessageBox.Show($"Could not change startup setting: {problem}",
                "Coder Mascot", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _cfg.StartWithWindows = enable;
        _cfg.Save();

        StartupChanged?.Invoke(this, enable);
    }

    /// <summary>Tick the switch without firing it — for a change made elsewhere.</summary>
    public void SetStartWithWindows(bool on) => MenuStartWithWindows.IsChecked = on;

    /// <summary>The startup setting was changed from this character's menu.</summary>
    public event EventHandler<bool>? StartupChanged;

    private void OnQuit(object sender, RoutedEventArgs e) =>
        System.Windows.Application.Current.Shutdown();

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
