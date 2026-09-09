using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CoderMascot.Core;

namespace CoderMascot.UI;

/// <summary>One looping animation: an ordered run of frames and a frame rate.</summary>
public sealed record SpriteAnimation(string Name, IReadOnlyList<ImageSource> Frames, int Fps)
{
    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, Fps));
}

/// <summary>
/// Loads the frames produced by tools/extract_sprites.py.
///
/// Frames are compiled in as WPF resources rather than copied next to the exe,
/// so a single-file publish stays genuinely single-file and the mascot can't
/// end up running with its artwork missing.
/// </summary>
public static class SpriteSet
{
    /// <summary>Frames are named 01.png, 02.png, … — stop at the first gap.</summary>
    private const int MaxFrames = 99;

    public static SpriteAnimation? Load(string character, string name, int fps)
    {
        var frames = new List<ImageSource>();

        for (var i = 1; i <= MaxFrames; i++)
        {
            var uri = new Uri($"pack://application:,,,/Assets/sprites/{character}/{name}/{i:D2}.png",
                              UriKind.Absolute);
            try
            {
                var info = System.Windows.Application.GetResourceStream(uri);
                if (info is null) break;

                using var stream = info.Stream;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // decode inside EndInit, before the stream closes
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();                                  // shareable across threads, cheaper to render

                frames.Add(bmp);
            }
            catch (IOException)
            {
                break;      // no such resource — this is the end of the animation
            }
            catch (Exception ex)
            {
                // A corrupt frame is NOT the end of the sequence. Skipping it
                // beats truncating the animation, and silently dropping to
                // vector art with no trace is how this stays unexplained.
                Debug.WriteLine($"[CoderMascot] sprite {name}/{i:D2} failed: {ex.Message}");
            }
        }

        return frames.Count > 0 ? new SpriteAnimation(name, frames, fps) : null;
    }

    /// <summary>
    /// Which animation plays for which state.
    ///
    /// hang = "clinging on", which is what every alarm state means; run = busy
    /// or racing a clock; idle = nothing to worry about.
    /// </summary>
    /// <summary>
    /// What to play when an animation has no frames of its own.
    ///
    /// Every animation here now has its own artwork, so none of these should
    /// fire. They are the safety net for a partial Assets/sprites tree: a
    /// mascot clinging to a wall in the wrong pose still beats one that
    /// vanishes, and the chain ends at `hang`, which is the alarm pose and
    /// therefore the one that must always exist.
    /// </summary>
    private static readonly Dictionary<string, string> Fallbacks = new()
    {
        ["climb"] = "climbidle",
        ["climbidle"] = "hang",
        ["fly"] = "flyidle",
        ["flyidle"] = "hover",
        ["ascend"] = "fly",
        ["descend"] = "fly",
        ["hover"] = "idle",
    };

    public static string Fallback(string name) =>
        Fallbacks.TryGetValue(name, out var alt) ? alt : name;

    /// <summary>
    /// What to play from the state alone, when nothing is driving the pose —
    /// patrol turned off, or the mascot parked.
    ///
    /// A flier can't hang off anything, so its alarm pose is a hover: still in
    /// the air, still holding position, waiting for you.
    /// </summary>
    public static string ForState(MascotState state, MovementStyle style) => state switch
    {
        MascotState.Connected => "idle",
        MascotState.Starting or MascotState.AutoStopSoon =>
            style == MovementStyle.Flies ? "fly" : "run",
        _ when state.IsAlarm() => style == MovementStyle.Flies ? "hover" : "hang",
        _ => "idle",
    };
}
