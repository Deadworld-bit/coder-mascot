using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CoderMascot.Core;

// UseWindowsForms implicitly imports System.Drawing, whose Point is an int
// struct — the wrong one for WPF's DIP coordinates.
using Point = System.Windows.Point;
using Screen = System.Windows.Forms.Screen;

namespace CoderMascot.UI;

/// <summary>
/// Walks the mascot around the edges of the desktop.
///
/// Position is a single scalar `s` — distance travelled clockwise around the
/// screen rectangle from its top-left corner. That collapses "which edge, at
/// what offset, facing which way" into one number, so movement is
/// `s += speed * dt` and corners need no special handling.
///
/// Motion itself is the health signal: while everything is fine the mascot
/// patrols, and when something needs attention it goes to a top corner and
/// stops. A mascot that has stopped moving is a mascot with something to say.
///
/// State is deliberately kept orthogonal — `_alertActive` (is the world bad),
/// `_held` (is the user touching it), `_enabled` (are we allowed to move) and
/// `_mode` (what am I doing). Folding those together is how an alert gets
/// silently cancelled by a stray click.
/// </summary>
public sealed class PatrolEngine : IDisposable
{
    private enum Mode { Walking, Idling, Summoning, Alerting }

    /// <summary>Beyond this, appear at the corner rather than jogging there.</summary>
    private const double JumpDistance = 1200;

    private readonly MascotWindow _window;
    private readonly CoderConfig _cfg;
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();

    private Mode _mode = Mode.Walking;
    private double _s;
    private int _dir = 1;
    private double _untilNextDecision;
    private double _targetS;
    private double _dpiScale = 1.0;
    private double _lastLoop;

    private bool _alertActive;
    private bool _held;
    private bool _enabled = true;

    /// <summary>Raised every time the mascot moves, so the bubble can follow.</summary>
    public event EventHandler<Rect>? Moved;

    public PatrolEngine(MascotWindow window, CoderConfig cfg)
    {
        _window = window;
        _cfg = cfg;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),   // ~30fps is plenty for a walk
        };
        _timer.Tick += OnTick;
    }

    private double Speed => Math.Clamp(_cfg.PatrolSpeed, 10, 600);

    // ---------- screen geometry ----------

    /// <summary>
    /// Every monitor's work area, in WPF device-independent pixels.
    ///
    /// Work area rather than full bounds, so the mascot walks *on* the taskbar
    /// instead of over it — and per-monitor, so each display's own taskbar
    /// counts. Screen reports physical pixels while Window.Left is DIPs, hence
    /// the DPI division; skipping it silently breaks every scaled display.
    /// </summary>
    private List<Rect> Screens()
    {
        var list = new List<Rect>();

        try
        {
            foreach (var s in Screen.AllScreens)
            {
                var w = s.WorkingArea;
                list.Add(new Rect(w.Left / _dpiScale, w.Top / _dpiScale,
                                  w.Width / _dpiScale, w.Height / _dpiScale));
            }
        }
        catch
        {
            // Enumerating displays can throw during a session switch.
        }

        if (list.Count == 0) list.Add(SystemParameters.WorkArea);
        return list;
    }

    /// <summary>
    /// The rectangle to walk around: the bounding box of every monitor's work
    /// area, or just the primary one.
    ///
    /// The bounding box is not the same shape as the monitors unless they're
    /// identical and aligned, so parts of this perimeter cross dead space with
    /// no display behind it. <see cref="SkipDeadSpace"/> steps over those.
    /// </summary>
    private Rect Bounds
    {
        get
        {
            if (string.Equals(_cfg.PatrolBounds, "primary", StringComparison.OrdinalIgnoreCase))
                return SystemParameters.WorkArea;

            var screens = Screens();
            var r = screens[0];
            for (var i = 1; i < screens.Count; i++) r.Union(screens[i]);

            return r.Width < 1 || r.Height < 1 ? SystemParameters.WorkArea : r;
        }
    }

    private double Loop
    {
        get { var b = Bounds; return Core.Perimeter.Length(b.Width, b.Height); }
    }

    /// <summary>Screen rectangles as plain tuples, for the pure geometry helpers.</summary>
    private List<(double L, double T, double W, double H)> ScreenTuples()
    {
        var list = new List<(double, double, double, double)>();
        foreach (var s in Screens()) list.Add((s.Left, s.Top, s.Width, s.Height));
        return list;
    }

    private bool OnAnyScreen(double x, double y) =>
        Core.Perimeter.OnAnyScreen(x, y, ScreenTuples());

    /// <summary>Step over any stretch of perimeter with no display behind it.</summary>
    private void SkipDeadSpace()
    {
        var b = Bounds;
        _s = Core.Perimeter.SkipDeadSpace(
            _s, _dir, b.Left, b.Top, b.Width, b.Height, ScreenTuples());
    }

    // ---------- lifecycle ----------

    public void Start()
    {
        if (!_cfg.Patrol || !_window.HasSprites) return;

        RefreshDpi();
        _lastLoop = Loop;

        // Begin from wherever the mascot already is, so it doesn't teleport on
        // launch — it just walks on from its saved spot.
        _s = NearestS();
        SkipDeadSpace();
        _untilNextDecision = 2 + _rng.NextDouble() * 3;
        _mode = Mode.Walking;
        _timer.Start();
        Apply();
    }

    public void Stop() => _timer.Stop();

    private void RefreshDpi()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(_window);
            if (dpi.DpiScaleX > 0) _dpiScale = dpi.DpiScaleX;
        }
        catch
        {
            _dpiScale = 1.0;
        }
    }

    /// <summary>Displays changed — re-derive everything rather than trusting `_s`.</summary>
    public void OnDisplaysChanged()
    {
        RefreshDpi();

        var loop = Loop;
        if (_lastLoop > 0 && loop > 0)
        {
            // Keep the mascot roughly where it was proportionally, instead of
            // teleporting to an arbitrary point on the new perimeter.
            _s = Core.Perimeter.Wrap(_s * (loop / _lastLoop), loop);
        }
        _lastLoop = loop;

        if (_alertActive) RetargetAlert();
        SkipDeadSpace();
        Apply();
    }

    /// <summary>Enable/disable movement (menu toggle, hidden, full-screen app).</summary>
    public void SetEnabled(bool on)
    {
        _enabled = on;

        if (on)
        {
            if (!_cfg.Patrol || !_window.HasSprites) return;
            _timer.Start();
            Resume();
        }
        else
        {
            _timer.Stop();
            // Leaving it stopped mid-climb would strand the sprite clinging to a
            // wall that is no longer beside it, facing whichever way it last
            // turned — nothing else ever calls SetPose.
            _window.ResetPose();
        }
    }

    /// <summary>Something needs the user. Go to a top corner and stop there.</summary>
    public void SetAlert(bool alert)
    {
        // Record the world's state even when we can't act on it — otherwise a
        // click or a hidden window silently cancels the alarm.
        _alertActive = alert;

        if (!_cfg.Patrol || _held || !_enabled) return;

        if (alert)
        {
            if (_mode is Mode.Summoning or Mode.Alerting) return;
            RetargetAlert();
            _mode = Mode.Summoning;
            Apply();
        }
        else if (_mode is Mode.Summoning or Mode.Alerting)
        {
            _mode = Mode.Walking;
            _untilNextDecision = 1 + _rng.NextDouble() * 2;
            Apply();
        }
    }

    /// <summary>The user grabbed the mascot (or hovered it) — stop driving it.</summary>
    public void BeginHold() => _held = true;

    /// <summary>Released — snap back to the nearest edge and carry on.</summary>
    public void EndHold()
    {
        _held = false;
        if (!_cfg.Patrol || !_enabled) return;
        Resume();
    }

    /// <summary>Re-enter whatever the world currently demands.</summary>
    private void Resume()
    {
        _s = NearestS();
        SkipDeadSpace();

        if (_alertActive)
        {
            RetargetAlert();
            _mode = Mode.Summoning;
        }
        else
        {
            _mode = Mode.Walking;
            _untilNextDecision = 1.5 + _rng.NextDouble() * 2;
        }

        Apply();
    }

    // ---------- alert perch ----------

    /// <summary>
    /// Pick a top corner that is actually on a monitor.
    ///
    /// Deriving it from the bounding box instead would, on a setup where the
    /// screens aren't top-aligned, send the disconnect alarm to a corner with no
    /// display behind it — the mascot would hang somewhere invisible and the one
    /// thing this app exists to do would fail silently.
    /// </summary>
    private void RetargetAlert()
    {
        var host = HostScreen();
        var inset = _window.Width / 2 + 20;

        var left = FindNearestOnScreen(host.Left + inset, host.Top);
        var right = FindNearestOnScreen(host.Right - inset, host.Top);

        _targetS = Math.Abs(Delta(_s, left)) <= Math.Abs(Delta(_s, right)) ? left : right;
    }

    /// <summary>The monitor the mascot is currently on, else the primary one.</summary>
    private Rect HostScreen()
    {
        var cx = _window.Left + _window.Width / 2;
        var cy = _window.Top + _window.Height / 2;

        var screens = Screens();
        foreach (var s in screens)
        {
            if (cx >= s.Left && cx <= s.Right && cy >= s.Top && cy <= s.Bottom) return s;
        }

        return screens[0];
    }

    /// <summary>Closest point on the perimeter to (x, y) that is on a display.</summary>
    private double FindNearestOnScreen(double x, double y)
    {
        var b = Bounds;
        var loop = Loop;
        if (loop <= 0) return 0;

        var bestS = Core.Perimeter.Nearest(x, y, b.Left, b.Top, b.Width, b.Height);
        var bestD = double.MaxValue;
        var found = false;

        for (var s = 0.0; s < loop; s += 8.0)
        {
            var p = Core.Perimeter.Resolve(s, b.Left, b.Top, b.Width, b.Height);
            if (!OnAnyScreen(p.X, p.Y)) continue;

            var d = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
            if (d < bestD) { bestD = d; bestS = s; found = true; }
        }

        return found ? bestS : bestS;
    }

    // ---------- tick ----------

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_enabled || _held) return;

        const double dt = 0.033;
        var loop = Loop;

        // A monitor may have been plugged in or the resolution changed.
        if (_lastLoop > 0 && Math.Abs(loop - _lastLoop) > 1)
        {
            OnDisplaysChanged();
            return;
        }
        _lastLoop = loop;

        switch (_mode)
        {
            case Mode.Alerting:
                return;

            case Mode.Summoning:
            {
                var remaining = Delta(_s, _targetS);

                // Jogging across three monitors reads as nothing in particular.
                // Appearing at the corner reads as urgency.
                if (Math.Abs(remaining) > JumpDistance)
                {
                    _s = _targetS;
                    _mode = Mode.Alerting;
                    break;
                }

                var step = Speed * 1.7 * dt;
                if (Math.Abs(remaining) <= step)
                {
                    _s = _targetS;
                    _mode = Mode.Alerting;
                }
                else
                {
                    _dir = Math.Sign(remaining);
                    _s = Core.Perimeter.Wrap(_s + step * _dir, loop);
                    SkipDeadSpace();
                }
                break;
            }

            case Mode.Walking:
                _s = Core.Perimeter.Wrap(_s + Speed * dt * _dir, loop);
                SkipDeadSpace();
                _untilNextDecision -= dt;
                if (_untilNextDecision <= 0) Decide();
                break;

            case Mode.Idling:
                _untilNextDecision -= dt;
                if (_untilNextDecision <= 0) Decide();
                break;
        }

        Apply();
    }

    /// <summary>Pick what to do next: keep walking, stop for a bit, turn around.</summary>
    private void Decide()
    {
        if (_rng.NextDouble() < 0.35) _dir = -_dir;

        if (_mode == Mode.Walking && _rng.NextDouble() < 0.4)
        {
            _mode = Mode.Idling;
            _untilNextDecision = 2 + _rng.NextDouble() * 4;
        }
        else
        {
            _mode = Mode.Walking;
            _untilNextDecision = 3 + _rng.NextDouble() * 6;
        }
    }

    /// <summary>Push the current position and pose onto the window.</summary>
    private void Apply()
    {
        // Never move the window while the user is holding it or the context menu
        // is open — a poll landing mid-drag would yank it out from under them.
        if (!_enabled || _held) return;

        var b = Bounds;
        var size = _window.Width;
        var p = Core.Perimeter.Resolve(_s, b.Left, b.Top, b.Width, b.Height);

        double left, top;
        switch (p.Edge)
        {
            case Edge.Top:
                left = p.X - size / 2;
                top = b.Top;
                break;
            case Edge.Bottom:
                left = p.X - size / 2;
                top = b.Bottom - size;
                break;
            case Edge.Right:
                left = b.Right - size;
                top = p.Y - size / 2;
                break;
            default:
                left = b.Left;
                top = p.Y - size / 2;
                break;
        }

        left = Math.Clamp(left, b.Left, Math.Max(b.Left, b.Right - size));
        top = Math.Clamp(top, b.Top, Math.Max(b.Top, b.Bottom - size));

        _window.MoveTo(left, top);

        var moving = _mode is Mode.Walking or Mode.Summoning;
        var style = _window.Character.Style;
        _window.SetPose(Core.Pose.Animation(style, p.Edge, moving, _dir),
                        Core.Pose.Mirrored(style, p.Edge, _dir));

        Moved?.Invoke(this, new Rect(left, top, size, _window.Height));
    }

    // ---------- perimeter maths (see Core/Perimeter.cs — unit-tested there) ----------

    /// <summary>
    /// Where the mascot is on the loop. Uses the window's CENTRE, because that's
    /// what <see cref="Apply"/> positions by — reading it back off the corner
    /// would shift `s` by half the window on every round trip.
    /// </summary>
    private double NearestS()
    {
        var b = Bounds;
        return Core.Perimeter.Nearest(
            _window.Left + _window.Width / 2,
            _window.Top + _window.Height / 2,
            b.Left, b.Top, b.Width, b.Height);
    }

    private double Delta(double a, double b) => Core.Perimeter.Delta(a, b, Loop);

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
