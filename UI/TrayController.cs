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
    private readonly ContextMenuStrip _menu;
    private readonly Func<MenuState> _menuState;
    private readonly TrayMenuRenderer _renderer = new();
    private readonly Font _font;
    private readonly Font _bold;
    private Icon? _current;
    private IntPtr _currentHandle;

    /// <summary>Last state we actually drew, so icons aren't rebuilt every poll.</summary>
    private MascotState _rendered = (MascotState)(-1);

    /// <summary>A line of the menu was chosen, or the icon was double-clicked.</summary>
    public event EventHandler<MenuEntry>? MenuChosen;

    /// <summary>
    /// One tray icon for the whole app, however many characters are on screen.
    ///
    /// The icon reports the workspace, and there is only one workspace — two
    /// icons showing the same colour would just be two things to explain. The
    /// menu is the same list the mascot's right-click menu shows, built from the
    /// same state each time it opens.
    /// </summary>
    public TrayController(Func<MenuState> menuState)
    {
        _menuState = menuState;

        // 9.75pt is the 13px the WPF menu uses, and SemiBold matches its bold row.
        _font = new Font("Segoe UI", 9.75f);
        _bold = new Font("Segoe UI Semibold", 9.75f);

        _menu = new ContextMenuStrip();
        Dress(_menu);

        // WinForms decides to cancel an empty strip before Opening runs, so it is
        // filled now and never empty; the refill on Opening keeps it current.
        Fill();
        _menu.Opening += (_, e) =>
        {
            Fill();
            e.Cancel = _menu.Items.Count == 0;
        };

        (_current, _currentHandle) = MakeIcon(Color.FromArgb(100, 116, 139));
        _icon = new NotifyIcon
        {
            Icon = _current,
            Visible = true,
            Text = "Coder Mascot — starting…",
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => MenuChosen?.Invoke(this, MenuEntry.OpenMain);

        // The strip is placed by its size before Opening; refilling on the
        // button going down means a row that came or went is already counted.
        _icon.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) Fill();
        };
    }

    /// <summary>Rebuilt each time it is about to open, so it can't disagree with the mascot's.</summary>
    private void Fill()
    {
        var old = _menu.Items.Cast<ToolStripItem>().ToList();
        _menu.Items.Clear();

        foreach (var entry in AppMenu.Build(_menuState())) _menu.Items.Add(ItemFor(entry));

        foreach (var item in old) item.Dispose();
    }

    private ToolStripItem ItemFor(MenuEntry entry)
    {
        if (entry.IsSeparator) return new ToolStripSeparator();

        // "&" marks a mnemonic in the text, and workspace, file and repository
        // names can hold one.
        var item = new ToolStripMenuItem(entry.Header.Replace("&", "&&"))
        {
            Enabled = entry.Enabled,
        };
        item.Padding = new Padding(item.Padding.Left, 4, item.Padding.Right, 4);

        if (entry.Bold) item.Font = _bold;
        if (entry.Tip is { } tip) item.ToolTipText = tip;

        // CheckOnClick stays off: the checks belong to the state the menu was
        // built from, and the shell toggling one by itself would put them out of
        // step with it.
        if (entry.Checked is { } on) item.Checked = on;

        if (entry.Items.Count > 0)
        {
            Dress(item.DropDown);
            foreach (var child in entry.Items) item.DropDownItems.Add(ItemFor(child));
        }
        else if (entry.Command != MenuCommand.None)
        {
            item.Click += (_, _) => MenuChosen?.Invoke(this, entry);
        }

        return item;
    }

    /// <summary>The strip and every submenu under it draw the same way.</summary>
    private void Dress(ToolStripDropDown drop)
    {
        drop.Renderer = _renderer;
        drop.Font = _font;
        drop.BackColor = _renderer.Back;
        drop.ForeColor = _renderer.Ink;
        drop.ShowItemToolTips = true;
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
                MascotState.PipelineFailed    => Color.FromArgb(249, 115, 98),

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
        _menu.Dispose();
        _bold.Dispose();
        _font.Dispose();
        _current?.Dispose();
        if (_currentHandle != IntPtr.Zero) DestroyIcon(_currentHandle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
