namespace CoderMascot.Core;

/// <summary>Which screen edge a point on the perimeter lies on.</summary>
public enum Edge { Top, Right, Bottom, Left }

public readonly record struct EdgePoint(double X, double Y, Edge Edge);

/// <summary>
/// Distance-around-the-rectangle geometry, in plain doubles.
///
/// Position on the loop is a single scalar `s` — distance travelled clockwise
/// from the top-left corner. Collapsing "which edge, at what offset, facing
/// which way" into one number makes movement `s += v * dt` and removes corner
/// handling entirely.
///
/// Deliberately free of WPF types so it can be unit-tested off-Windows; this is
/// the part of the patrol behaviour where an off-by-one would actually hide.
/// </summary>
public static class Perimeter
{
    public static double Length(double w, double h) => 2 * (w + h);

    public static double Wrap(double v, double period)
    {
        if (period <= 0 || double.IsNaN(v)) return 0;
        v %= period;
        return v < 0 ? v + period : v;
    }

    /// <summary>Map distance-around-the-edge to a point and the edge it's on.</summary>
    public static EdgePoint Resolve(double s, double left, double top, double w, double h)
    {
        var right = left + w;
        var bottom = top + h;
        s = Wrap(s, Length(w, h));

        if (s < w) return new EdgePoint(left + s, top, Edge.Top);

        s -= w;
        if (s < h) return new EdgePoint(right, top + s, Edge.Right);

        s -= h;
        if (s < w) return new EdgePoint(right - s, bottom, Edge.Bottom);

        s -= w;
        return new EdgePoint(left, bottom - s, Edge.Left);
    }

    /// <summary>
    /// Distance-around-the-edge of the closest perimeter point to (x, y).
    /// Used to put the mascot back on its track after the user drags it.
    /// </summary>
    public static double Nearest(double x, double y, double left, double top, double w, double h)
    {
        var right = left + w;
        var bottom = top + h;

        Span<(double Dist, double S)> candidates =
        [
            (Math.Abs(y - top),    Math.Clamp(x - left, 0, w)),
            (Math.Abs(x - right),  w + Math.Clamp(y - top, 0, h)),
            (Math.Abs(y - bottom), w + h + Math.Clamp(right - x, 0, w)),
            (Math.Abs(x - left),   2 * w + h + Math.Clamp(bottom - y, 0, h)),
        ];

        var best = candidates[0];
        foreach (var c in candidates)
        {
            if (c.Dist < best.Dist) best = c;
        }

        return Wrap(best.S, Length(w, h));
    }

    /// <summary>
    /// Is this point in front of an actual display?
    ///
    /// Tolerance matters: perimeter points sit exactly on the bounding box
    /// border, which is the *boundary* of whichever monitor touches it, and an
    /// exact-inclusion test would reject every one of them.
    /// </summary>
    public static bool OnAnyScreen(
        double x, double y,
        IReadOnlyList<(double L, double T, double W, double H)> screens,
        double tolerance = 2)
    {
        foreach (var s in screens)
        {
            if (x >= s.L - tolerance && x <= s.L + s.W + tolerance &&
                y >= s.T - tolerance && y <= s.T + s.H + tolerance)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Advance <paramref name="s"/> until it lands in front of a display.
    ///
    /// The bounding box of several monitors is only the same shape as the
    /// monitors when they are identical and aligned. Otherwise stretches of this
    /// perimeter cross empty space, and a mascot walking into one vanishes for
    /// tens of seconds — which reads as a crash, not as a feature.
    /// </summary>
    public static double SkipDeadSpace(
        double s, int dir,
        double left, double top, double w, double h,
        IReadOnlyList<(double L, double T, double W, double H)> screens,
        double step = 8)
    {
        var loop = Length(w, h);
        if (loop <= 0 || screens.Count == 0) return s;

        var delta = Math.Abs(step) * (dir >= 0 ? 1 : -1);
        var maxSteps = (int)(loop / Math.Abs(delta)) + 2;

        for (var i = 0; i < maxSteps; i++)
        {
            var p = Resolve(s, left, top, w, h);
            if (OnAnyScreen(p.X, p.Y, screens)) return s;
            s = Wrap(s + delta, loop);
        }

        return s;   // nowhere on the loop is on a screen; caller clamps anyway
    }

    /// <summary>
    /// Shortest signed distance from a to b around the loop. Sign gives the
    /// direction to travel, magnitude never exceeds half the perimeter.
    /// </summary>
    public static double Delta(double a, double b, double period)
    {
        if (period <= 0) return 0;
        var d = Wrap(b - a, period);
        return d > period / 2 ? d - period : d;
    }
}
