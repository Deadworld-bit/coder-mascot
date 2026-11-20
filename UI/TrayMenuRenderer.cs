using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CoderMascot.UI;

/// <summary>
/// Draws the tray icon's menu in the app's dark palette, row for row like the
/// mascot's WPF menu (UI/Menus.xaml).
///
/// The colours are read from the Theme.xaml brushes when this is built, not
/// copied: the palette stays declared in one place, and the two menus can't
/// drift apart one edit at a time.
/// </summary>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly Color _line;
    private readonly Color _hair;
    private readonly Color _lift;
    private readonly Color _dim;
    private readonly Color _faint;
    private readonly Color _pick;

    /// <summary>The menu surface, which the strips also set as their own back colour.</summary>
    public Color Back { get; }

    public Color Ink { get; }

    public TrayMenuRenderer()
    {
        Back = Theme("Head");
        _line = Theme("Line");
        _hair = Theme("Hair");
        _lift = Theme("Lift");
        Ink = Theme("Ink");
        _dim = Theme("Dim");
        _faint = Theme("Faint");
        _pick = Theme("Pick");

        RoundedEdges = false;
    }

    private static Color Theme(string key)
    {
        if (System.Windows.Application.Current?.Resources[key] is System.Windows.Media.SolidColorBrush brush)
        {
            var c = brush.Color;
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        // A missing key must not take the tray down; a menu in this colour is
        // ugly enough to be noticed and fixed.
        Debug.WriteLine($"[CoderMascot] theme brush '{key}' not found for the tray menu");
        return Color.Magenta;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var fill = new SolidBrush(Back);
        e.Graphics.FillRectangle(fill, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(_line);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    // Same colour as the rest of the menu, with no rule down the side.
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var fill = new SolidBrush(Back);
        e.Graphics.FillRectangle(fill, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        if (!item.Enabled || !(item.Selected || item.Pressed)) return;

        var g = e.Graphics;
        var row = new Rectangle(3, 1, item.Width - 6, item.Height - 2);

        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = Rounded(row, 4);
        using var fill = new SolidBrush(_lift);
        g.FillPath(fill, path);

        g.SmoothingMode = smoothing;
    }

    // Drawn here so both colours are the theme's own, whatever the base class
    // would pick for a disabled row.
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        TextRenderer.DrawText(
            e.Graphics, e.Text, e.TextFont, e.TextRectangle,
            e.Item.Enabled ? Ink : _dim, e.TextFormat);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        if (e.Vertical)
        {
            base.OnRenderSeparator(e);
            return;
        }

        var y = e.Item.Height / 2;
        using var pen = new Pen(_hair);
        e.Graphics.DrawLine(pen, 6, y, e.Item.Width - 6, y);
    }

    // A bare tick in the selection colour, the shape the WPF menu draws; no box behind it.
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        var k = Math.Max(1f, r.Height / 16f);
        var x = r.Left + r.Width / 2f;
        var y = r.Top + r.Height / 2f;

        var g = e.Graphics;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(_pick, 1.75f * k)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen,
        [
            new PointF(x - 5f * k, y + 0.5f * k),
            new PointF(x - 1.5f * k, y + 4f * k),
            new PointF(x + 5f * k, y - 3f * k),
        ]);

        g.SmoothingMode = smoothing;
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if (e.Direction != ArrowDirection.Right)
        {
            e.ArrowColor = _faint;
            base.OnRenderArrow(e);
            return;
        }

        var r = e.ArrowRectangle;
        var k = Math.Max(1f, r.Height / 16f);
        var x = r.Left + r.Width / 2f;
        var y = r.Top + r.Height / 2f;

        var g = e.Graphics;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(e.Item is { Enabled: false } ? _dim : _faint, 1.5f * k)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen,
        [
            new PointF(x - 2f * k, y - 4f * k),
            new PointF(x + 2f * k, y),
            new PointF(x - 2f * k, y + 4f * k),
        ]);

        g.SmoothingMode = smoothing;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
