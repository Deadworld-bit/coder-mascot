using System.Windows;
using CoderMascot.Core;

namespace CoderMascot.UI;

/// <summary>
/// One character on screen: its window, its speech bubble and its patrol.
///
/// Everything here is per-character; nothing in it talks to Coder. That split is
/// the whole design — a second mascot is a second thing to *draw*, not a second
/// thing to poll, and two characters must never mean two `coder ssh` calls every
/// thirty seconds.
/// </summary>
public sealed class MascotCrewMember : IDisposable
{
    public MascotWindow Window { get; }
    public BubbleWindow Bubble { get; }
    public PatrolEngine Patrol { get; }

    public Character Character => Window.Character;

    /// <summary>The user wants this one on screen (its tray toggle).</summary>
    public bool Wanted { get; set; } = true;

    public MascotCrewMember(CoderConfig cfg, Character character)
    {
        Window = new MascotWindow(cfg, character);
        Bubble = new BubbleWindow { HideFromCapture = cfg.HideFromScreenCapture };
        Patrol = new PatrolEngine(Window, cfg);
    }

    public Rect Frame => new(Window.Left, Window.Top, Window.Width, Window.Height);

    public void Dispose()
    {
        Patrol.Dispose();
        Bubble.Close();
    }
}
