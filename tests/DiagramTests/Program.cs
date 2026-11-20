using System.Text.Json;
using System.Text.Json.Serialization;
using CoderMascot.Core;

int failures = 0;
void Check(string desc, bool ok)
{
    if (!ok) { failures++; Console.WriteLine($"FAIL: {desc}"); }
}

DiagramPlace? Place(string? raw) => DiagramLink.Inspect(raw).Place;
string? Problem(string? raw) => DiagramLink.Inspect(raw).Problem;

// --- what may be opened (Inspect) ---
{
    Check("a draw.io file on this PC", Place(@"D:\work\db.drawio") == new DiagramPlace(@"D:\work\db.drawio", DiagramKind.File));
    Check("Explorer's Copy as path quotes come off", Place("\"D:\\work\\db.drawio\"")?.Target == @"D:\work\db.drawio");
    Check("forward slashes and doubled separators are tidied",
        Place("D:/work//sub\\\\db.drawio")?.Target == @"D:\work\sub\db.drawio");
    Check("every draw.io extension, in any case",
        new[] { @"C:\a.DRAWIO", @"C:\a.dio", @"C:\a.drawio.svg", @"C:\a.Drawio.Png" }.All(p => Place(p) is { Kind: DiagramKind.File }));

    Check("a program is refused", Place(@"D:\work\run.exe") is null);
    Check("a draw.io name with a program behind it is refused", Place(@"D:\work\db.drawio.exe") is null);
    Check("an alternate data stream is refused", Place(@"D:\work\run.exe:x.drawio") is null);
    Check("a plain svg is not a draw.io file", Place(@"D:\work\logo.svg") is null);
    Check("wildcards are refused", Place(@"D:\work\*.drawio") is null);
    Check("control characters are refused", Place("D:\\work\\a\u0007.drawio") is null);
    Check("a share is refused", Place(@"\\server\share\db.drawio") is null && Place("//server/share/db.drawio") is null);
    Check("a relative path is refused", Place("db.drawio") is null && Place("D:db.drawio") is null);
    Check("a workspace path gets told why",
        Problem("/home/coder/workspace/projects/AdminHub/docs/db.drawio")?.Contains("workspace path") == true);
    Check("nothing is a problem, not a crash", Problem(null) is not null && Problem("   ") is not null);

    var gitlab = Place("https://git.example.com/team/app/-/blob/main/docs/db.drawio.svg");
    Check("an https page", gitlab is { Kind: DiagramKind.Web });
    Check("http is refused", Place("http://git.example.com/db.drawio") is null);
    Check("credentials in a link are refused", Place("https://trusted.example.com@evil.example/db.drawio") is null);
    Check("a file URI is refused", Place("file:///D:/work/db.drawio") is null);
    Check("a script URI is refused", Place("javascript:alert(1)") is null);
    Check("other schemes are refused", Place("ftp://example.com/db.drawio") is null);

    Check("a trailing dot Windows would strip is refused", Place(@"C:\a.drawio.") is null);
    Check("an NT namespace path is refused", Place(@"\??\C:\a.drawio") is null && Place(@"\\?\C:\a.drawio") is null);
    Check("a path dressed as a URL is refused", Place(@"C:\a://b.drawio") is null);
    Check("a link that carries the diagram is refused",
        Place("https://app.diagrams.net/#R7VZNb9sw") is null);
    Check("an over-long link is refused", Place("https://example.com/" + new string('a', DiagramLink.MaxUrl)) is null);
    Check("create= is only refused on draw.io's own site",
        Place("https://app.diagrams.net/?create=abc") is null && Place("https://git.example.com/x?create=1") is not null);
    Check("a page anchor is not a payload", Place("https://example.com/doc#readme") is not null);

    foreach (var raw in new[] { "\"D:/work//db.drawio\"", "https://Git.Example.com/a/../b/db.drawio.svg?x=1#L2" })
    {
        var once = Place(raw)!.Target;
        Check($"cleaning is stable for {raw}", Place(once)?.Target == once);
    }
}

// --- what the chip says (Label) ---
{
    Check("the file name, without the extension",
        new DiagramLink { Target = @"D:\work\AdminHub-DB.drawio" }.Label == "AdminHub-DB");
    Check("the whole draw.io extension comes off", new DiagramLink { Target = @"D:\db.drawio.svg" }.Label == "db");
    Check("the last part of a link",
        new DiagramLink { Target = "https://git.example.com/team/app/-/blob/main/docs/db.drawio.svg" }.Label == "db");
    Check("a link with no path is named by its host",
        new DiagramLink { Target = "https://app.diagrams.net/#Uhttps%3A%2F%2Fexample.com%2Fdb.drawio" }.Label == "app.diagrams.net");
    Check("a given name wins", new DiagramLink { Title = "Sign DB", Target = @"D:\db.drawio" }.Label == "Sign DB");
    Check("a name is one line", DiagramLink.CleanTitle("Sign\r\n\tDB ") == "Sign DB");
    Check("and not a paragraph", DiagramLink.CleanTitle(new string('x', 200))?.Length == DiagramLink.MaxTitle);
}

// --- a project's list (TidyAll) ---
{
    var input = new DiagramLink?[]
    {
        new() { Title = " Main ", Target = "D:/work/db.drawio" },
        new() { Target = @"d:\WORK\DB.drawio" },
        new() { Target = @"D:\work\run.exe" },
        null,
        new() { Target = "https://example.com/A.drawio" },
        new() { Target = "https://example.com/a.drawio" },
    };

    var kept = DiagramLink.TidyAll(input);
    Check("unusable rows go, the same file twice is one row, URLs keep their case",
        kept is { Length: 3 } && kept[0].Target == @"D:\work\db.drawio" && kept[0].Title == "Main");
    Check("the input is left alone", input[0]!.Target == "D:/work/db.drawio" && input[0]!.Title == " Main ");
    Check("none is null, so the key stays out of the file",
        DiagramLink.TidyAll([]) is null && DiagramLink.TidyAll(null) is null);
}

// --- carried with the project ---
{
    var json = """
        [{ "name": "app", "path": "/home/coder/app", "group": "Sign",
           "diagrams": [{ "title": "DB", "target": "D:\\work\\db.drawio" }, { "target": "C:\\run.exe" }] },
         { "name": "tool", "path": "/home/coder/tool" }]
        """;

    var entries = CoderConfig.NormalizeProjects(JsonSerializer.Deserialize<ProjectEntry[]>(json));
    var app = entries[0];

    Check("read from the file, through the sieve", app.Diagrams is { Length: 1 } d && d[0].Title == "DB");
    Check("and onto the project the window shows", app.ToProject().Diagrams.Single().Target == @"D:\work\db.drawio");
    Check("a project without diagrams has an empty list", entries[1].ToProject().Diagrams.Count == 0);

    var opts = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    Check("and writes no key for them", !JsonSerializer.Serialize(entries[1], opts).Contains("diagrams"));
    Check("the label is never written", !JsonSerializer.Serialize(app, opts).Contains("label", StringComparison.OrdinalIgnoreCase));

    var edited = ProjectDraft.From(app);
    edited.Name = "renamed";
    edited.Group = "Other";
    Check("editing the repository keeps its diagrams", edited.ToEntry().Diagrams is { Length: 1 } kept && kept[0].Title == "DB");

    var added = app.WithDiagrams([.. app.Diagrams!, new DiagramLink { Target = "https://example.com/erd" }]);
    Check("a new list is a copy", added.Diagrams!.Length == 2 && app.Diagrams!.Length == 1);
    Check("which keeps everything else", added.Name == "app" && added.Group == "Sign" && added.Path == app.Path);
    Check("emptied, it is null again", app.WithDiagrams([]).Diagrams is null);
}

// --- the mascot's Diagrams menu (DiagramMenu) ---
{
    GitProject Repo(string name, string? group, params string[] targets) => new()
    {
        Name = name, Path = "/home/coder/" + name, Group = group,
        Diagrams = [.. targets.Select(t => new DiagramLink { Target = t })],
    };

    Check("nothing linked is an empty menu",
        DiagramMenu.Build(null).Count == 0 && DiagramMenu.Build([Repo("bare", null)]).Count == 0);

    var flat = DiagramMenu.Build([Repo("app", null, @"D:\app\db.drawio"), Repo("bare", null)]);
    Check("one diagram opens straight from the repository's line",
        flat is [{ Header: "app \u00b7 db", Target: @"D:\app\db.drawio", Items.Count: 0 }]);

    var two = DiagramMenu.Build([Repo("app", null, @"D:\app\db.drawio", "https://example.com/flow.drawio.svg")]);
    Check("two diagrams get a submenu under the repository",
        two is [{ Header: "app", Target: null, Items: [{ Header: "db" }, { Header: "flow" }] }]);

    var grouped = DiagramMenu.Build([
        Repo("loose", null, @"D:\loose.drawio"),
        Repo("sign-api", "Sign", @"D:\sign\db.drawio"),
        Repo("hub", "Hub", @"D:\hub\db.drawio"),
        Repo("sign-ui", "sign", @"D:\sign\ui.drawio"),
        Repo("empty", "Nobody", @"D:\run.exe"),
    ]);
    Check("grouped the way the rail is, loose ones last, empty groups left out",
        grouped.Select(g => g.Header).SequenceEqual(["Sign", "Hub", ProjectGroups.LooseTitle]));
    Check("a group holds its repositories in order, whatever the case it was typed in",
        grouped[0].Items.Select(i => i.Header).SequenceEqual(["sign-api \u00b7 db", "sign-ui \u00b7 ui"]));

    var single = DiagramMenu.Build([Repo("a", "Sign", @"D:\a.drawio"), Repo("b", "Sign", @"D:\b.drawio")]);
    Check("one group is not a level of its own", single.Count == 2 && single[0].Target == @"D:\a.drawio");
}

// --- through the real settings file (Save, then Load) ---
{
    var home = Path.Combine(Path.GetTempPath(), "diagram-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(home);   // GetFolderPath answers "" for a folder that isn't there
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home);

    // Only if the app's folder really moved: never write over a real config.
    if (!CoderConfig.Dir.StartsWith(home, StringComparison.Ordinal))
    {
        Check("the settings folder can be pointed at a scratch folder", false);
    }
    else
    {
        try
        {
            var cfg = new CoderConfig();
            cfg.SetProjects([
                new ProjectEntry { Name = "app", Path = "/home/coder/app",
                                   Diagrams = [new DiagramLink { Title = "DB", Target = "D:/work/db.drawio" }] },
                new ProjectEntry { Name = "tool", Path = "/home/coder/tool" },
            ]);
            cfg.Save();

            var text = File.ReadAllText(CoderConfig.Path_);
            var reloaded = CoderConfig.Load();

            Check("a diagram survives Save and Load",
                reloaded.Projects[0].ToProject().Diagrams.Single() is { Title: "DB", Target: @"D:\work\db.drawio" });
            Check("written once, and only for the project that has one",
                text.Split("\"diagrams\"").Length == 2 && reloaded.Projects[1].Diagrams is null);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }
}

Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILED");
return failures;
