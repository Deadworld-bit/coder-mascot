namespace CoderMascot.Core;

/// <summary>What choosing a menu line does. The renderers don't know; App decides.</summary>
public enum MenuCommand
{
    None,
    SessionWatchHelp,
    Dismiss,
    Alerts,
    OpenMain,
    Projects,
    Diagram,
    NewNote,
    CheckNow,
    OpenCoder,
    Connect,
    EditConfig,
    ToggleMascot,
    Patrol,
    ClickThrough,
    StartWithWindows,
    Quit,
}

/// <summary>
/// One line of the right-click menu: a command to run, a submenu with lines under
/// it, or a separator. The header is plain text; escaping it for the toolkit that
/// draws it is the renderer's job.
/// </summary>
public sealed record MenuEntry
{
    public string Header { get; init; } = "";

    public MenuCommand Command { get; init; }

    /// <summary>Which one: an alert policy, a character id or a diagram target.</summary>
    public string? Argument { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Null when the line has no tick to show; otherwise whether it is ticked.</summary>
    public bool? Checked { get; init; }

    public bool Bold { get; init; }

    public string? Tip { get; init; }

    public IReadOnlyList<MenuEntry> Items { get; init; } = [];

    public bool IsSeparator { get; init; }

    public static readonly MenuEntry Separator = new() { IsSeparator = true };

    /// <summary>The line a double-click stands for, so the gesture and the menu share one command.</summary>
    public static readonly MenuEntry OpenMain = new()
    {
        Header = "Open Coder Mascot…",
        Command = MenuCommand.OpenMain,
        Bold = true,
    };
}

/// <summary>A character as the menu lists it, in crew order.</summary>
public sealed record MenuCharacter(string Id, string Display, bool Visible);

/// <summary>
/// Everything the menu depends on. One of these is built each time a menu is
/// about to open, so the mascot's menu and the tray's can't disagree.
/// </summary>
public sealed record MenuState(
    string Status,
    bool SessionWatchMissing,
    bool Dismissable,
    AlertPolicy Alerts,
    IReadOnlyList<MenuCharacter> Characters,
    bool Patrol,
    bool ClickThrough,
    bool StartWithWindows,
    IReadOnlyList<DiagramMenuItem> Diagrams);

/// <summary>
/// The one menu list, for the mascot's right-click menu and the tray icon's.
/// </summary>
public static class AppMenu
{
    public static IReadOnlyList<MenuEntry> Build(MenuState s)
    {
        var menu = new List<MenuEntry>
        {
            new() { Header = s.Status, Enabled = false },
        };

        // The session watch needs a workspace-side install, and until that has
        // happened it reports nothing at all — which is indistinguishable from
        // reporting that all is well. A watchdog is allowed to be quiet; it is
        // not allowed to be quiet because nobody plugged it in.
        if (s.SessionWatchMissing)
            menu.Add(new() { Header = "Session watch: not set up", Command = MenuCommand.SessionWatchHelp });

        menu.Add(MenuEntry.Separator);

        // Grey when there is nothing to know about: an always-live dismiss is a
        // button that mostly does nothing, and one that does nothing is one you
        // stop believing in. It is here, and not only a click on the mascot,
        // because the mascot can be hidden, click-through, or off behind a
        // full-screen app — states in which poking it is not an option.
        menu.Add(new()
        {
            Header = "I know about this — stop telling me",
            Command = MenuCommand.Dismiss,
            Enabled = s.Dismissable,
        });

        // A radio group: exactly one is ticked. The menu is rebuilt on every
        // open, so a click never has to re-tick the other two.
        menu.Add(new()
        {
            Header = "Messages",
            Items =
            [
                Policy(s, AlertPolicy.All, "Show everything"),
                Policy(s, AlertPolicy.Quiet, "Quiet — no pop-ups, still comes to get you"),
                Policy(s, AlertPolicy.Off, "Off — badge colour only"),
            ],
        });

        menu.Add(MenuEntry.Separator);
        menu.Add(MenuEntry.OpenMain);
        menu.Add(new() { Header = "Projects…", Command = MenuCommand.Projects });
        menu.Add(Diagrams(s.Diagrams));
        menu.Add(new() { Header = "New sticky note", Command = MenuCommand.NewNote });
        menu.Add(new() { Header = "Check now", Command = MenuCommand.CheckNow });
        menu.Add(new() { Header = "Open Coder dashboard", Command = MenuCommand.OpenCoder });
        menu.Add(new() { Header = "Connect to Coder…", Command = MenuCommand.Connect });
        menu.Add(new() { Header = "Edit config… (restart to apply)", Command = MenuCommand.EditConfig });
        menu.Add(MenuEntry.Separator);

        foreach (var c in s.Characters)
        {
            menu.Add(new()
            {
                Header = s.Characters.Count == 1 ? "Show mascot" : $"Show {c.Display}",
                Command = MenuCommand.ToggleMascot,
                Argument = c.Id,
                Checked = c.Visible,
            });
        }

        menu.Add(new() { Header = "Walk around the edges", Command = MenuCommand.Patrol, Checked = s.Patrol });
        menu.Add(new() { Header = "Click-through", Command = MenuCommand.ClickThrough, Checked = s.ClickThrough });
        menu.Add(new() { Header = "Start with Windows", Command = MenuCommand.StartWithWindows, Checked = s.StartWithWindows });
        menu.Add(MenuEntry.Separator);
        menu.Add(new() { Header = "Quit", Command = MenuCommand.Quit });

        return menu;
    }

    private static MenuEntry Policy(MenuState s, AlertPolicy policy, string header) => new()
    {
        Header = header,
        Command = MenuCommand.Alerts,
        Argument = policy.ToString(),
        Checked = s.Alerts == policy,
    };

    private static MenuEntry Diagrams(IReadOnlyList<DiagramMenuItem> tree)
    {
        if (tree.Count > 0)
            return new() { Header = "Diagrams", Items = [.. tree.Select(Diagram)] };

        return new()
        {
            Header = "Diagrams",
            Items =
            [
                new() { Header = "No diagrams linked yet", Enabled = false },
                new() { Header = "Link one in Projects…", Command = MenuCommand.Projects },
            ],
        };
    }

    private static MenuEntry Diagram(DiagramMenuItem item)
    {
        if (item.Target is { } target)
            return new() { Header = item.Header, Command = MenuCommand.Diagram, Argument = target, Tip = target };

        return new() { Header = item.Header, Items = [.. item.Items.Select(Diagram)] };
    }
}
