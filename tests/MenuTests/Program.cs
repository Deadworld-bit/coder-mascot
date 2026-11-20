using CoderMascot.Core;

int failures = 0;
void Check(string desc, bool ok)
{
    if (!ok) { failures++; Console.WriteLine($"FAIL: {desc}"); }
}

MenuState State(
    string status = "Connected — my-ws",
    bool missing = false,
    bool dismissable = false,
    AlertPolicy alerts = AlertPolicy.All,
    IReadOnlyList<MenuCharacter>? crew = null,
    bool patrol = true,
    bool clickThrough = false,
    bool startup = false,
    IReadOnlyList<DiagramMenuItem>? diagrams = null) =>
    new(status, missing, dismissable, alerts,
        crew ?? [new MenuCharacter("fox", "Fox", true)],
        patrol, clickThrough, startup, diagrams ?? []);

List<MenuEntry> Build(MenuState s) => AppMenu.Build(s).ToList();

MenuEntry? Find(IReadOnlyList<MenuEntry> menu, string header) => menu.FirstOrDefault(e => e.Header == header);

string Shape(IReadOnlyList<MenuEntry> menu) =>
    string.Join(" | ", menu.Select(e => e.IsSeparator ? "---" : e.Header));

var open = new[]
{
    DiagramMenuItem.Folder("Backend",
    [
        DiagramMenuItem.Open("api · db", @"D:\work\db.drawio"),
        DiagramMenuItem.Folder("web", [DiagramMenuItem.Open("flow", @"D:\work\flow.drawio")]),
    ]),
};

// --- the list, in order ---
{
    var menu = Build(State());

    Check("the full list, in order, for one character with nothing wrong",
        Shape(menu) == "Connected — my-ws | --- | I know about this — stop telling me | Messages | --- | "
            + "Open Coder Mascot… | Projects… | Diagrams | New sticky note | Check now | Open Coder dashboard | "
            + "Connect to Coder… | Edit config… (restart to apply) | --- | Show mascot | Walk around the edges | "
            + "Click-through | Start with Windows | --- | Quit");

    Check("the commands, in order",
        string.Join(",", menu.Where(e => !e.IsSeparator).Select(e => e.Command)) ==
        "None,Dismiss,None,OpenMain,Projects,None,NewNote,CheckNow,OpenCoder,Connect,EditConfig,"
        + "ToggleMascot,Patrol,ClickThrough,StartWithWindows,Quit");

    Check("quit is last", menu[^1].Command == MenuCommand.Quit && menu[^2].IsSeparator);
    Check("a separator never leads, trails or doubles",
        !menu[0].IsSeparator && !menu[^1].IsSeparator
        && !menu.Zip(menu.Skip(1)).Any(p => p.First.IsSeparator && p.Second.IsSeparator));
}

// --- the status line ---
{
    var menu = Build(State(status: "Workspace down — my_ws & co"));
    Check("the status is first, disabled, and plain text (escaping is the renderer's)",
        menu[0].Header == "Workspace down — my_ws & co" && !menu[0].Enabled && menu[0].Command == MenuCommand.None);
}

// --- session watch ---
{
    var fine = Build(State());
    var missing = Build(State(missing: true));

    Check("no session-watch line when it is installed", Find(fine, "Session watch: not set up") is null);

    var line = Find(missing, "Session watch: not set up");
    Check("the line appears when it is missing, right under the status",
        line is { Command: MenuCommand.SessionWatchHelp, Enabled: true } && missing.IndexOf(line) == 1);
    Check("it adds exactly that one line", missing.Count == fine.Count + 1);
}

// --- dismiss ---
{
    Check("dismiss is greyed when there is nothing to dismiss",
        Find(Build(State(dismissable: false)), "I know about this — stop telling me") is { Enabled: false, Command: MenuCommand.Dismiss });
    Check("dismiss is live when there is",
        Find(Build(State(dismissable: true)), "I know about this — stop telling me") is { Enabled: true, Command: MenuCommand.Dismiss });
}

// --- messages ---
{
    foreach (var policy in Enum.GetValues<AlertPolicy>())
    {
        var messages = Find(Build(State(alerts: policy)), "Messages")!;
        var ticked = messages.Items.Where(i => i.Checked == true).ToList();

        Check($"Messages has three choices ({policy})",
            messages.Items.Count == 3 && messages.Items.All(i => i.Command == MenuCommand.Alerts && i.Checked is not null));
        Check($"exactly one is ticked, and it is the policy in force ({policy})",
            ticked.Count == 1 && ticked[0].Argument == policy.ToString());
    }

    var all = Find(Build(State()), "Messages")!.Items;
    Check("the arguments name the policies, in the order shown",
        string.Join(",", all.Select(i => i.Argument)) == "All,Quiet,Off");
    Check("the Messages line itself does nothing", Find(Build(State()), "Messages")!.Command == MenuCommand.None);
}

// --- characters ---
{
    var one = Build(State());
    Check("one character is just 'Show mascot'",
        one.Count(e => e.Command == MenuCommand.ToggleMascot) == 1 && Find(one, "Show mascot") is { Argument: "fox", Checked: true });

    var crew = new[]
    {
        new MenuCharacter("fox", "Fox", true),
        new MenuCharacter("owl", "Owl", false),
        new MenuCharacter("cat", "Cat", true),
    };
    var many = Build(State(crew: crew));
    var shows = many.Where(e => e.Command == MenuCommand.ToggleMascot).ToList();

    Check("several characters get one line each, in crew order",
        string.Join(",", shows.Select(e => e.Header)) == "Show Fox,Show Owl,Show Cat");
    Check("the ids are the arguments", string.Join(",", shows.Select(e => e.Argument)) == "fox,owl,cat");
    Check("the ticks follow visibility", string.Join(",", shows.Select(e => e.Checked)) == "True,False,True");
    Check("the Show lines sit together, before Walk around the edges",
        many.IndexOf(shows[0]) + 2 == many.IndexOf(shows[2])
        && many[many.IndexOf(shows[2]) + 1].Header == "Walk around the edges"
        && many[many.IndexOf(shows[0]) - 1].IsSeparator);
}

// --- the three switches ---
{
    var off = Build(State(patrol: false, clickThrough: false, startup: false));
    var on = Build(State(patrol: true, clickThrough: true, startup: true));

    Check("the switches are checkable and show the state they were built from",
        new[] { "Walk around the edges", "Click-through", "Start with Windows" }
            .All(h => Find(off, h)!.Checked == false && Find(on, h)!.Checked == true));
    Check("they carry their own commands",
        Find(on, "Walk around the edges")!.Command == MenuCommand.Patrol
        && Find(on, "Click-through")!.Command == MenuCommand.ClickThrough
        && Find(on, "Start with Windows")!.Command == MenuCommand.StartWithWindows);
}

// --- diagrams ---
{
    var menu = Build(State(diagrams: open));
    var diagrams = Find(menu, "Diagrams")!;

    Check("Diagrams holds the tree", diagrams.Items.Count == 1 && diagrams.Items[0].Header == "Backend" && diagrams.Items[0].Items.Count == 2);

    var leaf = diagrams.Items[0].Items[0];
    Check("a leaf opens its target, and says so in the tip",
        leaf is { Command: MenuCommand.Diagram, Argument: @"D:\work\db.drawio", Tip: @"D:\work\db.drawio" } && leaf.Items.Count == 0);

    var nested = diagrams.Items[0].Items[1];
    Check("a heading has no command and keeps its children",
        nested is { Command: MenuCommand.None, Argument: null } && nested.Header == "web"
        && nested.Items.Count == 1 && nested.Items[0] is { Command: MenuCommand.Diagram, Argument: @"D:\work\flow.drawio" });

    Check("the Diagrams line itself does nothing", diagrams.Command == MenuCommand.None);
}

{
    var diagrams = Find(Build(State()), "Diagrams")!;

    Check("with no diagrams: a disabled placeholder, then a way to add one",
        diagrams.Items.Count == 2
        && diagrams.Items[0] is { Header: "No diagrams linked yet", Enabled: false, Command: MenuCommand.None }
        && diagrams.Items[1] is { Header: "Link one in Projects…", Enabled: true, Command: MenuCommand.Projects });
}

// --- emphasis and labels ---
{
    var menu = Build(State(missing: true, dismissable: true, diagrams: open));

    static IEnumerable<MenuEntry> Walk(IEnumerable<MenuEntry> entries) =>
        entries.SelectMany(e => new[] { e }.Concat(Walk(e.Items)));

    var bold = Walk(menu).Where(e => e.Bold).ToList();
    Check("only 'Open Coder Mascot…' is bold",
        bold.Count == 1 && bold[0] is { Header: "Open Coder Mascot…", Command: MenuCommand.OpenMain });

    var headers = Walk(menu).Select(e => e.Header).ToList();
    Check("the old labels are gone",
        !headers.Contains("Dashboard…") && !headers.Contains("Branches…") && !headers.Contains("Link one in Branches…"));

    Check("a double-click is the same line as the menu's", menu.Contains(MenuEntry.OpenMain));
}

// --- one list ---
{
    var state = State(missing: true, dismissable: true, alerts: AlertPolicy.Quiet, diagrams: open,
        crew: [new MenuCharacter("fox", "Fox", true), new MenuCharacter("owl", "Owl", false)]);

    Check("the same state always gives the same list", Shape(Build(state)) == Shape(Build(state)));
}

Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILED");
return failures;
