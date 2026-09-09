using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using CoderMascot.Core;

namespace CoderMascot.UI;

/// <summary>
/// Notification-area icon: the always-visible fallback when the mascot is
/// hidden or click-through. Icons are drawn at runtime, so the app ships with
/// no image assets at all.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly IReadOnlyList<MascotWindow> _mascots;
    private readonly ToolStripMenuItem _clickThroughItem;
    private readonly ToolStripMenuItem _dismissItem;
    private readonly Dictionary<AlertPolicy, ToolStripMenuItem> _alertItems = new();
    private Icon? _current;
    private IntPtr _currentHandle;

    /// <summary>Last state we actually drew, so icons aren't rebuilt every poll.</summary>
    private MascotState _rendered = (MascotState)(-1);

    public event EventHandler? CheckRequested;

    /// <summary>Raised when a character is shown or hidden from the tray menu.</summary>
    public event EventHandler<(MascotWindow Mascot, bool Visible)>? VisibilityToggled;

    /// <summary>"I know about this one" — the same thing clicking the mascot does.</summary>
    public event EventHandler? Acknowledged;

    /// <summary>A new noise level was picked from the tray menu.</summary>
    public event EventHandler<AlertPolicy>? AlertPolicyChanged;

    /// <summary>Open the detail window.</summary>
    public event EventHandler? DashboardRequested;

    /// <summary>Write something down — the detail window, on the notes.</summary>
    public event EventHandler? NoteRequested;

    /// <summary>Connect this app to a Coder deployment.</summary>
    public event EventHandler? SetupRequested;

    /// <summary>Where work has got to, across the watched repositories.</summary>
    public event EventHandler? BranchesRequested;

    /// <summary>
    /// One tray icon for the whole app, however many characters are on screen.
    ///
    /// The icon reports the workspace, and there is only one workspace — two
    /// icons showing the same colour would just be two things to explain. What
    /// is per-character is visibility, so that gets an entry each.
    /// </summary>
    public TrayController(IReadOnlyList<MascotWindow> mascots)
    {
        _mascots = mascots;

        var showItems = new List<ToolStripItem>();
        foreach (var mascot in mascots)
        {
            var label = mascots.Count == 1 ? "Show mascot" : $"Show {mascot.Character.Display}";
            var item = new ToolStripMenuItem(label) { Checked = true, CheckOnClick = true };
            item.Click += (_, _) =>
            {
                mascot.Visibility = item.Checked
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Hidden;

                // AllowsTransparency forces software rendering, so looping
                // storyboards behind a hidden window burn CPU for nothing.
                mascot.SetAnimating(item.Checked);
                VisibilityToggled?.Invoke(this, (mascot, item.Checked));
            };
            showItems.Add(item);
        }

        _clickThroughItem = new ToolStripMenuItem("Click-through") { CheckOnClick = true };
        _clickThroughItem.Click += (_, _) =>
        {
            foreach (var m in _mascots) m.SetClickThrough(_clickThroughItem.Checked);
        };

        var dashboardItem = new ToolStripMenuItem("Dashboard…")
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };
        dashboardItem.Click += (_, _) => DashboardRequested?.Invoke(this, EventArgs.Empty);

        var noteItem = new ToolStripMenuItem("New sticky note");
        noteItem.Click += (_, _) => NoteRequested?.Invoke(this, EventArgs.Empty);

        var checkItem = new ToolStripMenuItem("Check now");
        checkItem.Click += (_, _) => CheckRequested?.Invoke(this, EventArgs.Empty);

        var branchesItem = new ToolStripMenuItem("Branches…");
        branchesItem.Click += (_, _) => BranchesRequested?.Invoke(this, EventArgs.Empty);

        var setupItem = new ToolStripMenuItem("Connect to Coder…");
        setupItem.Click += (_, _) => SetupRequested?.Invoke(this, EventArgs.Empty);

        // The mascot can be hidden, click-through, or off behind a full-screen
        // app — all states in which poking it is not an option. This is the
        // route that always exists.
        _dismissItem = new ToolStripMenuItem("I know about this — stop telling me")
        {
            Enabled = false,
        };
        _dismissItem.Click += (_, _) => Acknowledged?.Invoke(this, EventArgs.Empty);

        var messagesItem = new ToolStripMenuItem("Messages");
        foreach (var (policy, label) in new (AlertPolicy, string)[]
                 {
                     (AlertPolicy.All, "Show everything"),
                     (AlertPolicy.Quiet, "Quiet — no pop-ups, still comes to get you"),
                     (AlertPolicy.Off, "Off — badge colour only"),
                 })
        {
            // CheckOnClick off on purpose: these three are a radio group, and
            // letting the shell toggle one independently would allow all three
            // ticked at once, or none.
            var item = new ToolStripMenuItem(label) { Checked = policy == AlertPolicy.All };
            item.Click += (_, _) =>
            {
                SetAlertPolicy(policy);
                AlertPolicyChanged?.Invoke(this, policy);
            };
            _alertItems[policy] = item;
            messagesItem.DropDownItems.Add(item);
        }

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([.. showItems]);
        menu.Items.AddRange(new ToolStripItem[]
        {
            dashboardItem, branchesItem, noteItem, _clickThroughItem, checkItem,
            new ToolStripSeparator(), _dismissItem, messagesItem, setupItem,
            new ToolStripSeparator(), quitItem,
        });

        (_current, _currentHandle) = MakeIcon(Color.FromArgb(100, 116, 139));
        _icon = new NotifyIcon
        {
            Icon = _current,
            Visible = true,
            Text = "Coder Mascot — starting…",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => DashboardRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Render(WorkspaceSnapshot snap, bool notify)
    {
        // Only redraw on an actual state change — Render runs on every poll.
        if (snap.State != _rendered)
        {
            _rendered = snap.State;

            var color = snap.State switch
            {
                MascotState.Connected     => Color.FromArgb(74, 222, 128),
                MascotState.Starting      => Color.FromArgb(56, 189, 248),
                MascotState.AutoStopSoon  => Color.FromArgb(251, 191, 36),
                MascotState.NeedsConfirmation => Color.FromArgb(244, 114, 182),
                MascotState.SessionStalled    => Color.FromArgb(251, 146, 60),
                MascotState.ResourcesHigh     => Color.FromArgb(234, 179, 8),
                MascotState.Leftovers         => Color.FromArgb(45, 212, 191),
                MascotState.Unreachable   => Color.FromArgb(249, 115, 22),
                MascotState.Unauthorized  => Color.FromArgb(167, 139, 250),
                MascotState.AgentLost     => Color.FromArgb(239, 68, 68),
                MascotState.WorkspaceDown => Color.FromArgb(239, 68, 68),
                _                         => Color.FromArgb(100, 116, 139),
            };

            var (icon, handle) = MakeIcon(color);
            SwapIcon(icon, handle);
        }

        // NotifyIcon.Text is capped at 63 characters by the shell.
        var tip = $"{snap.State.Title()} — {snap.Detail}";
        _icon.Text = tip.Length > 62 ? tip[..62] : tip;

        _clickThroughItem.Checked = _mascots.Count > 0 && _mascots[0].IsClickThrough;

        if (notify)
        {
            _icon.ShowBalloonTip(
                10_000,
                snap.State.Title(),
                snap.Detail,
                snap.State.IsAlarm() ? ToolTipIcon.Error
                    : snap.State == MascotState.AutoStopSoon ? ToolTipIcon.Warning
                    : ToolTipIcon.Info);
        }
    }

    /// <summary>Tick the noise level that's actually in force.</summary>
    public void SetAlertPolicy(AlertPolicy policy)
    {
        foreach (var (key, item) in _alertItems) item.Checked = key == policy;
    }

    /// <summary>Is there anything to dismiss right now?</summary>
    public void SetDismissable(bool on) => _dismissItem.Enabled = on;

    /// <summary>
    /// Raise a balloon that isn't tied to a state change — the repeat nudge for
    /// something already on screen and still unanswered.
    /// </summary>
    public void Notify(string title, string detail)
    {
        try
        {
            _icon.ShowBalloonTip(10_000, title, detail, ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            // The shell refuses balloons in some states (focus assist, a
            // just-disposed icon). A missed reminder is not worth a crash.
            Debug.WriteLine($"[CoderMascot] balloon refused: {ex.Message}");
        }
    }

    private void SwapIcon(Icon next, IntPtr nextHandle)
    {
        var prevIcon = _current;
        var prevHandle = _currentHandle;

        _current = next;
        _currentHandle = nextHandle;
        _icon.Icon = next;

        // Only safe to free the old handle once the shell is showing the new one.
        prevIcon?.Dispose();
        if (prevHandle != IntPtr.Zero) DestroyIcon(prevHandle);
    }

    /// <summary>
    /// The character at 32px with a state-coloured badge in the corner. Falls
    /// back to a plain coloured dot if the sprite can't be loaded — the tray
    /// icon is the app's only always-visible indicator, so it must always draw
    /// something meaningful.
    ///
    /// The handle must NOT be destroyed here. Icon.Clone() does not deep-copy an
    /// icon built by Icon.FromHandle — it shares the same HICON — so destroying
    /// it eagerly would hand the shell a dangling handle and the tray icon
    /// would render blank.
    /// </summary>
    private static (Icon Icon, IntPtr Handle) MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            var face = TrayFace;
            if (face is not null)
            {
                g.DrawImage(face, 0, 0, 32, 32);
                using var badge = new SolidBrush(color);
                using var ring = new Pen(Color.FromArgb(190, 0, 0, 0), 1.5f);
                g.FillEllipse(badge, 19, 19, 12, 12);
                g.DrawEllipse(ring, 19, 19, 12, 12);
            }
            else
            {
                using var fill = new SolidBrush(color);
                g.FillEllipse(fill, 4, 4, 24, 24);
                using var ring = new Pen(Color.FromArgb(90, 0, 0, 0), 2f);
                g.DrawEllipse(ring, 4, 4, 24, 24);
            }
        }

        var handle = bmp.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    private static readonly Lazy<Bitmap?> LazyTrayFace =
        new(LoadTrayFace, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>One idle frame, loaded once, used as the tray icon's base.</summary>
    private static Bitmap? TrayFace => LazyTrayFace.Value;

    private static Bitmap? LoadTrayFace()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/sprites/idle/01.png", UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info is null) return null;

            using var stream = info.Stream;

            // Bitmap(Stream) does NOT take ownership — GDI+ keeps decoding from
            // the stream lazily and requires it to stay open for the bitmap's
            // whole life. Copy into a standalone bitmap so disposing the stream
            // here can't blow up a later DrawImage.
            using var decoded = new Bitmap(stream);
            return new Bitmap(decoded);
        }
        catch (Exception ex)
        {
            // The coloured-dot fallback covers this, but leave a trace — a
            // silent downgrade is invisible until someone wonders why the tray
            // icon looks plain.
            Debug.WriteLine($"[CoderMascot] tray face unavailable: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
        if (_currentHandle != IntPtr.Zero) DestroyIcon(_currentHandle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
