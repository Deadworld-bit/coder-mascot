using System.Windows.Media;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace CoderMascot.UI;

/// <summary>
/// Hex string in, frozen brush out, once per colour.
///
/// Frozen because these are handed to templates on every repaint: an unfrozen
/// brush is thread-affine and carries change notification nobody is listening
/// for, and a list of forty branches rebuilds several hundred of them.
/// </summary>
public static class Palette
{
    private static readonly Dictionary<string, Brush> Cache = [];

    public static Brush Of(string hex)
    {
        if (Cache.TryGetValue(hex, out var known)) return known;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        Cache[hex] = brush;
        return brush;
    }
}
