// Credential-handling checks for CoderConfig. These exist because a security
// review caught the app persisting a copy of the Coder CLI's live session token
// into its own config.json the first time the user dragged the mascot — a
// credential that then outlived `coder logout`. Nothing here may regress.
//
// Run:  XDG_CONFIG_HOME=$(mktemp -d) dotnet run -c Release
// Exit code is the number of failures.

using CoderMascot.Core;
using System.Text.Json;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (!ok) failures++;
}

void Reset()
{
    if (File.Exists(CoderConfig.Path_)) File.Delete(CoderConfig.Path_);
    Directory.CreateDirectory(CoderConfig.Dir);
}

string Disk() => File.ReadAllText(CoderConfig.Path_);

void WriteConfig(object o) => File.WriteAllText(CoderConfig.Path_, JsonSerializer.Serialize(o));

// --- A token borrowed from the CLI or environment must never reach our disk ---
Reset();
Environment.SetEnvironmentVariable("CODER_URL", "https://coder.example.com");
Environment.SetEnvironmentVariable("CODER_SESSION_TOKEN", "SECRET-CLI-TOKEN-123");

var borrowed = CoderConfig.Load();
Check("borrowed token is loaded into memory", borrowed.Token == "SECRET-CLI-TOKEN-123");
Check("borrowed values flagged external", borrowed.TokenIsExternal && borrowed.UrlIsExternal);

borrowed.MascotLeft = 100;
borrowed.Save();                       // what happens when you drag the mascot
Check("token NOT written to disk", !Disk().Contains("SECRET-CLI-TOKEN-123"));
Check("url NOT written to disk", !Disk().Contains("coder.example.com"));
Check("window position IS written to disk", Disk().Contains("100"));

// --- A token the user typed in themselves must survive a save ---
Reset();
Environment.SetEnvironmentVariable("CODER_URL", null);
Environment.SetEnvironmentVariable("CODER_SESSION_TOKEN", null);
WriteConfig(new { url = "https://coder.example.com", token = "USER-OWN-TOKEN" });

var owned = CoderConfig.Load();
Check("self-supplied token not flagged external", !owned.TokenIsExternal);
owned.Save();
Check("self-supplied token preserved", Disk().Contains("USER-OWN-TOKEN"));

// --- URL must be https (or loopback http), with no userinfo trick ---
(string Url, bool Accept)[] urls =
[
    ("https://coder.example.com", true),
    ("http://127.0.0.1:4820", true),
    ("http://coder.internal", false),               // token in cleartext on the LAN
    ("https://coder.example.com@evil.com", false),    // reads as trusted, resolves to evil.com
    ("file:///C:/x.exe", false),
    (@"\\attacker\share\payload.exe", false),       // UNC -> Process.Start gadget
    ("ms-msdt:/id", false),                         // registered protocol handler
];

foreach (var (url, accept) in urls)
{
    Reset();
    WriteConfig(new { url, token = "t" });
    var cfg = CoderConfig.Load();

    Check($"url {url,-42} -> {(accept ? "accepted" : "rejected")}",
        accept ? cfg.Url is not null
               : cfg.Url is null && cfg.RejectedUrl is not null);
}

// --- A corrupt session file must not throw inside HttpClient during startup ---
Reset();
WriteConfig(new { url = "https://x.io", token = "bad\r\nInjected: 1" });
Check("header-injecting token dropped", CoderConfig.Load().Token is null);

// --- the character list ------------------------------------------------------
//
// This decides how many windows open. A bad value here doesn't throw: it starts
// the app with no mascot at all, or with two stacked in the same place both
// writing the same saved position.

Reset();
WriteConfig(new { url = "https://x.io", characters = new[] { "charizard", "pikachu" } });
Check("both characters are kept, in order",
    CoderConfig.Load().Characters.SequenceEqual(new[] { "charizard", "pikachu" }));

Reset();
WriteConfig(new { url = "https://x.io", characters = new[] { "pikachu", "PIKACHU", "pikachu" } });
Check("duplicates collapse to one window",
    CoderConfig.Load().Characters.SequenceEqual(new[] { "pikachu" }));

Reset();
WriteConfig(new { url = "https://x.io", characters = new[] { "mewtwo", "pikachu" } });
Check("an unknown character is dropped, not fatal",
    CoderConfig.Load().Characters.SequenceEqual(new[] { "pikachu" }));

foreach (var (label, list) in new (string, string[])[]
         { ("empty", []), ("all unknown", ["mewtwo"]) })
{
    Reset();
    WriteConfig(new { url = "https://x.io", characters = list });
    Check($"{label} list still yields a mascot", CoderConfig.Load().Characters.Length == 1);
}

// --- position migration -------------------------------------------------------
//
// The old single-mascot keys must carry over, or upgrading silently teleports
// the mascot back to the default corner.
Reset();
WriteConfig(new { url = "https://x.io", mascotLeft = 640.0, mascotTop = 480.0 });
var migrated = CoderConfig.Load();
Check("the old saved position moves to the first character",
    migrated.MascotPositions.TryGetValue("pikachu", out var at) &&
    at is [640.0, 480.0]);

// ...but must never overwrite a position already recorded per character.
Reset();
WriteConfig(new
{
    url = "https://x.io",
    mascotLeft = 640.0,
    mascotTop = 480.0,
    mascotPositions = new Dictionary<string, double[]> { ["pikachu"] = [10, 20] },
});
Check("a per-character position wins over the legacy one",
    CoderConfig.Load().MascotPositions["pikachu"] is [10.0, 20.0]);

// --- stall thresholds ---------------------------------------------------------
//
// Thinking is the impatient case by definition. Configured the other way round
// it silently never fires: a thinking session would hit the tool threshold
// first and be reported as the patient kind.
Reset();
WriteConfig(new
{
    url = "https://x.io",
    sessionStalledSeconds = 300,
    sessionThinkingStalledSeconds = 900,
});
var thresholds = CoderConfig.Load();
Check("thinking patience can never exceed tool patience",
    thresholds.SessionThinkingStalledSeconds <= thresholds.SessionStalledSeconds);

// --- machine load ------------------------------------------------------------
//
// None of this can fail loudly. It either cries wolf during every build, or
// flaps on and off at the threshold, and both make the warning worthless in a
// way you only discover by living with it.

{
    // A build pegs the CPU for a minute. That must not be a notification.
    var w = new LoadWatch(cpuLimit: 88, memoryLimit: 88, sustainSeconds: 120);
    var fired = false;
    for (var t = 0; t <= 90; t += 10) fired |= w.Update(new LoadSample(100, 40), t);
    Check("a 90-second build does not warn", !fired);

    // Sustained past the threshold, it should.
    Check("but two minutes of it does", w.Update(new LoadSample(100, 40), 130));

    // And it must not clear the instant load dips a point below the line.
    Check("a momentary dip doesn't clear it", w.Update(new LoadSample(85, 40), 140));
    Check("a real drop does", !w.Update(new LoadSample(50, 40), 150));
}

{
    // Flapping: a machine parked exactly at the line must not toggle the
    // warning on and off every poll.
    var w = new LoadWatch(88, 88, sustainSeconds: 0);
    w.Update(new LoadSample(40, 90), 0);
    var flips = 0;
    var last = true;
    for (var t = 1; t <= 40; t++)
    {
        var now = w.Update(new LoadSample(40, t % 2 == 0 ? 88 : 86), t);
        if (now != last) flips++;
        last = now;
    }
    Check("hovering at the threshold doesn't flap", flips == 0);
}

{
    var w = new LoadWatch(88, 88, sustainSeconds: 0);
    w.Update(new LoadSample(40, 95), 0);
    Check("memory alone is enough", w.MemoryOver && !w.CpuOver);
}

// The whole point of the feature: name what to close.
{
    var procs = new (string, long)[]
    {
        ("Code", 1_400_000_000), ("Code", 1_300_000_000), ("Code", 1_200_000_000),
        ("chrome", 900_000_000), ("chrome", 800_000_000),
        ("notepad", 12_000_000),
    };
    var culprits = LoadReport.Group(procs);
    Check("processes group by name", culprits[0].Name == "Code" && culprits[0].Count == 3);
    Check("biggest family first", culprits[0].MemoryGb > culprits[1].MemoryGb);
    Check("trivial processes are left out", culprits.All(c => c.Name != "notepad"));

    var text = LoadReport.Describe(new LoadSample(30, 92), false, true, culprits);
    Check("the message names the culprit and the count", text.Contains("Code ×3"));
    Check("the message says which resource", text.Contains("92%"));
}

// --- reminders ---------------------------------------------------------------
//
// The failure here is being annoying, which no test can judge — but the shape
// of the schedule can be pinned down: front-loaded, then backing off, then
// settling rather than growing forever.

{
    var r = new Reminder(firstSeconds: 120, maxSeconds: 900);
    r.Begin(0);

    Check("silent before the first interval", !r.Due(119));
    Check("nudges at the first interval", r.Due(120));
    Check("then goes quiet again", !r.Due(121));

    // Gaps must grow, then stop: measured 240, 480, 900, 900, 900.
    var cursor = 120.0;
    var gaps = new List<double>();
    for (var i = 0; i < 5; i++)
    {
        var t = cursor;
        while (!r.Due(t) && t < cursor + 5000) t += 1;
        gaps.Add(t - cursor);
        cursor = t;
    }
    Check("each gap is longer than the last, until the cap",
        gaps[0] < gaps[1] && gaps[1] < gaps[2]);
    Check("the gap stops growing at the cap", gaps[^1] <= 900);

    // A backoff with no ceiling is the same as no reminder at all.
    Check("reminders never drift hours apart", gaps.All(g => g <= 900));
}

{
    var r = new Reminder(120, 900);
    r.Begin(0);
    r.Due(120);
    Check("the nudge says how long it has been", r.Text("Claude needs you", 600).Contains("10 min"));

    r.Clear();
    Check("nothing is due once it's answered", !r.Due(5000));
}

{
    // A fresh problem gets the full quiet period, not the previous one's
    // stretched-out gap.
    var r = new Reminder(120, 900);
    r.Begin(0);
    for (double t = 0; t < 100000 && r.Count < 4; t += 30) r.Due(t);
    r.Clear();
    r.Begin(10000);
    Check("a new subject restarts the backoff", !r.Due(10119) && r.Due(10120));
}

// Clamps: a reminder gap shorter than the first one would fire continuously.
Reset();
WriteConfig(new { url = "https://x.io", remindAfterSeconds = 600, remindMaxSeconds = 60 });
var rc = CoderConfig.Load();
Check("the reminder cap can't be shorter than the first gap",
    rc.RemindMaxSeconds >= rc.RemindAfterSeconds);

Reset();
WriteConfig(new { url = "https://x.io", remindAfterSeconds = 0 });
Check("zero still means off", CoderConfig.Load().RemindAfterSeconds == 0);

Reset();
WriteConfig(new { url = "https://x.io", cpuWarnPercent = 0.0, memoryWarnPercent = 250.0 });
var lc = CoderConfig.Load();
Check("a 0% threshold would warn forever, so it's clamped", lc.CpuWarnPercent >= 40);
Check("a >100% threshold would never warn, so it's clamped", lc.MemoryWarnPercent <= 99);

// --- what the mascot is allowed to interrupt with --------------------------
//
// The failure mode to guard against isn't noise, it's the opposite: a silenced
// mascot that also stops being a mascot. Dismissing an alarm must mute it, never
// clear it, and it must not carry over to the next thing that goes wrong.

{
    var g = new AlertGate();
    var down = MascotState.WorkspaceDown;

    Check("a fresh problem gets to speak", g.AllowToast(down) && g.AllowBubble(down));
    Check("and gets to come and stand in front of you", g.AllowPark(down));

    g.Acknowledge(down);
    Check("dismissed: no toast", !g.AllowToast(down));
    Check("dismissed: no bubble", !g.AllowBubble(down));
    Check("dismissed: no repeat nudges", !g.AllowReminder(down));
    Check("dismissed: it goes back to patrolling", !g.AllowPark(down));

    // The one thing dismissing must never do.
    Check("dismissing does not make it healthy", down.NeedsAttention());

    // A different problem is a different problem.
    g.Observe(MascotState.NeedsConfirmation);
    Check("the next problem is not pre-dismissed",
        g.AllowToast(MascotState.NeedsConfirmation));
    Check("and the first one is no longer dismissed either", g.AllowToast(down));
}

{
    // Dismissing while everything is fine would otherwise arm a silence that
    // swallows the next real occurrence of that state.
    var g = new AlertGate();
    g.Acknowledge(MascotState.Connected);
    Check("poking a healthy mascot dismisses nothing",
        !g.IsAcknowledged(MascotState.Connected));
}

{
    // Recovering and relapsing is the common case: Claude answers a prompt, then
    // hits another one twenty seconds later.
    var g = new AlertGate();
    g.Acknowledge(MascotState.NeedsConfirmation);
    g.Observe(MascotState.Connected);
    g.Observe(MascotState.NeedsConfirmation);
    Check("a second prompt after a good spell still interrupts",
        g.AllowToast(MascotState.NeedsConfirmation));
}

{
    var g = new AlertGate { Policy = AlertPolicy.Quiet };
    var down = MascotState.WorkspaceDown;

    Check("quiet: nothing is said", !g.AllowToast(down) && !g.AllowBubble(down));
    Check("quiet: no nudges either", !g.AllowReminder(down));

    // With the words gone, movement is the only signal left; silencing the text
    // must not silence that too, or "quiet" quietly becomes "off".
    Check("quiet: it still comes to get you", g.AllowPark(down));
}

{
    var g = new AlertGate { Policy = AlertPolicy.Off };
    Check("off: it stays out of the way entirely",
        !g.AllowPark(MascotState.WorkspaceDown));
}

// Good news is never a nudge — a reminder only exists for things left unanswered.
Check("nothing to be reminded of when all is well",
    !new AlertGate().AllowReminder(MascotState.Connected));

// An unreadable setting must land on the loudest option. Failing quiet here
// would disable the alarms of an app whose only job is to raise them.
foreach (var (text, expect) in new (string?, AlertPolicy)[]
         {
             ("all", AlertPolicy.All), ("quiet", AlertPolicy.Quiet),
             (" QUIET ", AlertPolicy.Quiet), ("off", AlertPolicy.Off),
             ("none", AlertPolicy.Off), ("quite", AlertPolicy.All),
             ("", AlertPolicy.All), (null, AlertPolicy.All),
         })
{
    Check($"notifications {text ?? "(missing)",-10} -> {AlertGate.Text(expect)}",
        AlertGate.Parse(text) == expect);
}

Reset();
WriteConfig(new { url = "https://x.io", notifications = "QUIET" });
Check("the setting is normalised on the way in",
    CoderConfig.Load().Notifications == "quiet");

Reset();
WriteConfig(new { url = "https://x.io", notifications = "shhh" });
Check("a typo doesn't silently mute the app",
    CoderConfig.Load().Notifications == "all");

// --- "Claude finished" --------------------------------------------------------
//
// The notification that was missing. Its two ways of being wrong are opposite
// and both bad: never firing (you sit there while the work is done), or firing
// on every poll for a session that finished an hour ago.

SessionLine Line(string id, string status, string? folder = null, int ranFor = 0) =>
    new() { Id = id, Status = status, Folder = folder, RanFor = ranFor };

{
    var before = SessionDiff.StatusById([Line("a", "running"), Line("b", "idle")]);
    var now = new[] { Line("a", "idle", "coder-mascot", 720), Line("b", "idle") };

    var done = SessionDiff.JustFinished(before, now);
    Check("a session that stopped running is announced", done.Count == 1 && done[0].Id == "a");
    Check("one that was already idle is not", done.All(d => d.Id != "b"));
    Check("the message says where and how long",
        SessionDiff.Describe(done).Contains("coder-mascot") &&
        SessionDiff.Describe(done).Contains("12 min"));

    // Second poll, nothing changed: it must not say it again.
    Check("it is not announced twice",
        SessionDiff.JustFinished(SessionDiff.StatusById(now), now).Count == 0);
}

{
    // Startup: everything is idle because nothing has been polled yet. Treating
    // that as news would greet you with a burst of finished-notifications for
    // work that finished before the app was running.
    var done = SessionDiff.JustFinished(
        new Dictionary<string, string>(), [Line("a", "idle"), Line("b", "idle")]);
    Check("nothing is announced on the very first reading", done.Count == 0);
}

{
    // A session parked at a prompt that you then answer and it completes.
    var before = SessionDiff.StatusById([Line("a", "waiting")]);
    Check("finishing after a prompt counts too",
        SessionDiff.JustFinished(before, [Line("a", "idle")]).Count == 1);
}

Check("a finished session reads as finished, not idle",
    Line("a", "idle", "x", 300).Activity.Contains("Finished"));
Check("a thinking session says so",
    new SessionLine { Id = "a", Status = "running", Phase = "thinking" }.Activity.Contains("Thinking"));
Check("a waiting session quotes Claude's own words",
    new SessionLine { Id = "a", Status = "waiting", Message = "Run rm -rf build?" }
        .Activity == "Run rm -rf build?");

// --- things left running -------------------------------------------------------
//
// Classification is by owning runtime, not by port. The failure this avoids is
// warning about the machine's own services, which is how you teach someone to
// ignore the feature within a day.

Check("a node dev server counts", DevServers.LooksLikeDevServer("node.exe", 5173));
Check("so does dotnet watch", DevServers.LooksLikeDevServer("dotnet", 5000));
Check("so does uvicorn", DevServers.LooksLikeDevServer("uvicorn", 8000));
Check("a Windows service does not", !DevServers.LooksLikeDevServer("svchost.exe", 5040));
Check("nor does SQL Server", !DevServers.LooksLikeDevServer("sqlservr", 1433));
Check("a privileged port is never ours", !DevServers.LooksLikeDevServer("node", 443));

{
    DevServer Server(string name, int port, double hours) => new()
    {
        Port = port, Pid = port, Process = name, Uptime = TimeSpan.FromHours(hours),
    };

    var all = new[] { Server("node", 5173, 6), Server("dotnet", 5000, 0.2), Server("python", 8000, 3) };
    var stale = DevServers.Stale(all, afterMinutes: 120);

    // The age gate is the whole feature: you are supposed to have one running
    // while you work.
    Check("the one you just started is left alone", stale.All(s => s.Port != 5000));
    Check("the old ones are named", stale.Count == 2);
    Check("oldest first", stale[0].Port == 5173);
    Check("the message names the worst offender",
        DevServers.Describe(stale).Contains("node :5173") && DevServers.Describe(stale).Contains("6h"));
    Check("turning it off reports nothing", DevServers.Stale(all, 0).Count == 0);
}

{
    var now = DateTimeOffset.UtcNow;

    Check("a workspace used a minute ago is not idle",
        LeftoverWatch.WorkspaceIdleFor(now.AddMinutes(-1), now, 45) is null);
    Check("one untouched for an hour is",
        LeftoverWatch.WorkspaceIdleFor(now.AddMinutes(-60), now, 45) is not null);
    Check("zero minutes turns it off",
        LeftoverWatch.WorkspaceIdleFor(now.AddDays(-1), now, 0) is null);
    Check("never-used reports nothing rather than forever",
        LeftoverWatch.WorkspaceIdleFor(null, now, 45) is null);

    // A clock skewed the other way would otherwise read as a huge idle time and
    // offer to stop a workspace somebody is sitting in.
    Check("a last-used stamp in the future is ignored",
        LeftoverWatch.WorkspaceIdleFor(now.AddMinutes(30), now, 45) is null);

    var report = LeftoverWatch.Build(TimeSpan.FromMinutes(90), []);
    Check("an idle workspace alone is worth mentioning", report.Any);
    Check("and says how long", report.Detail.Contains("1h"));
    Check("nothing at all is not a warning", !LeftoverWatch.Build(null, []).Any);
}

// Leftovers must never outrank a real problem, and must never park the mascot.
Check("things left running is not an alarm", !MascotState.Leftovers.IsAlarm());
Check("nor does it drag the mascot into a corner", !MascotState.Leftovers.NeedsAttention());

// --- recent activity ----------------------------------------------------------

{
    var h = new StateHistory();
    h.Record(MascotState.Connected, "All good.");
    h.Record(MascotState.Connected, "All good.");
    Check("the poll loop does not fill the history", h.Recent.Count == 1);

    h.Record(MascotState.AgentLost, "Agent disconnected.");
    Check("newest first", h.Recent[0].State == MascotState.AgentLost);

    for (var i = 0; i < 200; i++) h.Note($"note {i}");
    Check("the history is bounded", h.Recent.Count <= 50);
}

// --- notes ---------------------------------------------------------------------
//
// The one thing this app stores that the user cannot get back: everything else
// on disk is a setting with a default, a note is not.

{
    var dir = Directory.CreateTempSubdirectory("mascot-notes").FullName;
    var path = Path.Combine(dir, "notes.json");

    var book = new NoteBook(path);
    Check("a blank note is not a note", book.Add("   ") is null);

    var first = book.Add("port 5173 is the vite one")!;
    var second = book.Add("ask BA about the wording on the sign screen")!;
    Check("both notes are kept", book.Count() == 2);

    // Stamped by hand: two notes added in the same millisecond would otherwise
    // make this assert about the clock's resolution rather than about the rule.
    second.Updated = first.Updated.AddMinutes(1);
    Check("newest first", book.All()[0].Id == second.Id);

    book.SetPinned(first.Id, true);
    Check("a pinned note comes first however old it is", book.All()[0].Id == first.Id);

    book.SetDone(first.Id, true);
    Check("a finished note sinks below the unfinished ones, pinned or not",
        book.All()[^1].Id == first.Id);
    Check("open and done are counted apart", book.OpenCount() == 1 && book.DoneCount() == 1);

    Check("search finds by content", book.Search("VITE").Count == 1);
    Check("a blank search is not a filter", book.Search("  ").Count == 2);

    Check("notes are written", book.Save());

    var reread = NoteBook.Load(path);
    Check("and read back whole", reread.Count() == 2);
    Check("with pinned and done intact",
        reread.Find(first.Id) is { Pinned: true, Done: true });
    Check("and the text unchanged",
        reread.Find(second.Id)?.Text == "ask BA about the wording on the sign screen");

    // Clearing the text is how a note is deleted — but only once the caret has
    // left it, which is what Prune is for.
    reread.SetText(second.Id, "");
    Check("an emptied note survives until it is pruned", reread.Count() == 2);
    Check("pruning drops it", reread.Prune() == 1 && reread.Count() == 1);

    reread.ClearDone();
    Check("clearing done empties the book", reread.Count() == 0);

    // A file we cannot parse must be moved aside, never written over: the bytes
    // are the only copy of something the user typed.
    File.WriteAllText(path, "{ this is not json");
    var rescued = NoteBook.Load(path);
    Check("a corrupt notes file starts empty", rescued.Count() == 0);
    Check("and says so", rescued.LastError is { Length: > 0 });
    Check("and the original bytes are kept",
        Directory.GetFiles(dir, "notes-unreadable-*.json").Length == 1);

    rescued.Add("still usable afterwards");
    Check("a new file is written in its place", rescued.Save() && File.Exists(path));

    Directory.Delete(dir, recursive: true);
}

// --- a value we refuse is not a value we may delete ---
//
// Both of these are nulled in memory on purpose, so they can't be used. Writing
// that null back to disk would erase what the user typed on the first save the
// app happens to make — and dragging the mascot one pixel makes one.

Reset();
Environment.SetEnvironmentVariable("CODER_URL", null);
Environment.SetEnvironmentVariable("CODER_SESSION_TOKEN", null);
WriteConfig(new { url = "https://x.io", token = "bad token with spaces" });

var refused = CoderConfig.Load();
Check("a token that can't go in a header is not used", refused.Token is null);
refused.Save();
Check("but it is not erased either", Disk().Contains("bad token with spaces"));

Reset();
WriteConfig(new { url = "ftp://x.io/", token = "T" });
var badUrl = CoderConfig.Load();
Check("a rejected URL is not used", badUrl.Url is null && badUrl.RejectedUrl == "ftp://x.io");
badUrl.Save();
Check("and survives the save so it can be corrected", Disk().Contains("ftp://x.io"));

// --- one pile per purpose -------------------------------------------------------
//
// A note belongs to exactly one list. Filing takes one decision; that is the
// point of lists rather than tags on something that has to be faster to use
// than opening a text file.

{
    var dir = Directory.CreateTempSubdirectory("mascot-groups").FullName;
    var path = Path.Combine(dir, "notes.json");

    var book = new NoteBook(path);
    Check("there is always a list to write into", book.Groups.Count == 1);

    Check("a new list is created", book.AddGroup("CONTRACT-APP"));
    Check("the same list twice is not two lists", !book.AddGroup("contract-app"));
    Check("a blank name is not a list", !book.AddGroup("   "));

    var general = book.Add("laptop battery is dying")!;
    book.ActiveGroup = "CONTRACT-APP";
    var contract = book.Add("UserDocumentSigningMethod is per signer")!;

    Check("a note lands in the list on screen", contract.Group == "CONTRACT-APP");
    Check("and the other list is untouched", general.Group == NoteBook.DefaultGroup);
    Check("each list counts only its own", book.Count("CONTRACT-APP") == 1 && book.Count() == 2);
    Check("a search inside a list stays inside it",
        book.Search("", "CONTRACT-APP").Count == 1);

    Check("a note can be refiled", book.MoveTo(general.Id, "CONTRACT-APP"));
    Check("refiling to a list that doesn't exist does nothing",
        !book.MoveTo(general.Id, "Nowhere"));
    Check("and the count follows it", book.Count("CONTRACT-APP") == 2);

    book.SetDone(general.Id, true);
    book.ActiveGroup = NoteBook.DefaultGroup;
    Check("clearing done in one list leaves the others alone",
        book.ClearDone(NoteBook.DefaultGroup) == 0 && book.Count() == 2);

    Check("a list can be renamed", book.RenameGroup("CONTRACT-APP", "Contract-App"));
    Check("its notes come with it", book.Count("Contract-App") == 2);
    Check("renaming onto an existing name is refused rather than merging",
        !book.RenameGroup("Contract-App", NoteBook.DefaultGroup));

    book.ActiveGroup = "Contract-App";
    Check("the list on screen is remembered", book.Save() &&
        NoteBook.Load(path).ActiveGroup == "Contract-App");

    // Deleting a heading must never be a quiet way to delete the notes under it.
    Check("a list can be deleted", book.RemoveGroup("Contract-App"));
    Check("its notes move rather than die", book.Count() == 2);
    Check("and land somewhere real", book.All().All(n => n.Group == NoteBook.DefaultGroup));
    Check("the last list cannot be deleted", !book.RemoveGroup(NoteBook.DefaultGroup));
    Check("deleting the list on screen falls back to All", book.ActiveGroup is null);

    // Upgrading from the version that had one flat pile.
    File.WriteAllText(path, """
        [ { "id": "abc", "text": "from the old format", "pinned": true } ]
        """);
    var upgraded = NoteBook.Load(path);
    Check("notes written before lists existed are still read", upgraded.Count() == 1);
    Check("and land in the default list",
        upgraded.Find("abc") is { Pinned: true, Group: NoteBook.DefaultGroup });

    // The file is meant to be hand-editable, so a note may name a list nobody
    // declared. The name someone typed is the intent.
    File.WriteAllText(path, """
        { "groups": ["General"], "notes": [ { "id": "z", "text": "x", "group": "Invented" } ] }
        """);
    var invented = NoteBook.Load(path);
    Check("a hand-written list name creates the list",
        invented.Groups.Contains("Invented") && invented.Count("Invented") == 1);

    // A list name longer than the cap is stored truncated. The note has to be
    // filed under the *stored* name, or it belongs to a list that does not
    // exist: invisible under every tab, and present only in the totals.
    var longName = new string('L', NoteBook.MaxGroupNameLength + 12);
    File.WriteAllText(path,
        @"{ ""groups"": [], ""notes"": [ { ""id"": ""q"", ""text"": ""keep me"", ""group"": """
        + longName + @""" } ] }");

    var capped = NoteBook.Load(path);
    var stored = capped.Groups.First(g => g.StartsWith('L'));
    Check("an over-long list name is capped", stored.Length == NoteBook.MaxGroupNameLength);
    Check("and its note is filed under the capped name, not orphaned",
        capped.Count(stored) == 1 && capped.Find("q")?.Group == stored);

    // An app that closed between the edit and the sweep leaves one behind.
    File.WriteAllText(path,
        @"{ ""groups"": [""General""], ""notes"": [ { ""id"": ""e"", ""text"": ""   "" } ] }");
    Check("an empty note left on disk is not read back as a blank row",
        NoteBook.Load(path).Count() == 0);

    Directory.Delete(dir, recursive: true);
}

// --- sticky notes -------------------------------------------------------------
//
// A note on the desktop is a window the user drags around and a monitor they may
// unplug. Both rules below are about not losing one.

{
    var screen = new Area(0, 0, 1920, 1080);

    var kept = StickyPlacement.Clamp(new Area(300, 200, 240, 200), screen);
    Check("a note where you left it is left where it is",
        kept == new Area(300, 200, 240, 200));

    // Dropped mostly off the right edge, on purpose: that is allowed, because
    // you can still see and grab it.
    var edged = StickyPlacement.Clamp(new Area(1850, 400, 240, 200), screen);
    Check("a note hanging off the edge stays hanging off the edge", edged.Left == 1850);

    var lost = StickyPlacement.Clamp(new Area(4000, 3000, 240, 200), screen);
    Check("a note on a monitor that is gone comes back",
        lost.Left <= screen.Right && lost.Top <= screen.Bottom);
    Check("and is still grabbable", lost.Right >= screen.Left + 60 && lost.Bottom >= screen.Top);

    var above = StickyPlacement.Clamp(new Area(200, -400, 240, 200), screen);
    Check("a note dragged above the top comes down — its header is the only handle",
        above.Top >= screen.Top);

    var tiny = StickyPlacement.Clamp(new Area(10, 10, 20, 10), screen);
    Check("a note cannot be shrunk to nothing",
        tiny.Width >= StickyPlacement.MinWidth && tiny.Height >= StickyPlacement.MinHeight);

    var broken = StickyPlacement.Clamp(new Area(double.NaN, double.NaN, double.NaN, double.NaN), screen);
    Check("a note with no size at all gets the default one",
        broken.Width == StickyPlacement.DefaultWidth && broken.Height == StickyPlacement.DefaultHeight);

    var first = StickyPlacement.Near(500, 500, screen);
    var second = StickyPlacement.Near(500, 500, screen, alreadyOpen: 1);
    Check("a new note is not opened under the pointer", first.Left > 500 && first.Top > 500);
    Check("and a second one does not land exactly on the first", second.Left != first.Left);

    Check("an unknown colour falls back rather than painting garbage",
        NoteColour.Of("chartreuse").Id == NoteColour.Default);
    Check("colours cycle back round",
        NoteColour.Next(NoteColour.All[^1].Id) == NoteColour.All[0].Id);
}

{
    var dir = Directory.CreateTempSubdirectory("mascot-sticky").FullName;
    var path = Path.Combine(dir, "notes.json");

    var book = new NoteBook(path);
    var note = book.Add("the deploy window is 16:00")!;
    book.SetStuck(note.Id, true);
    book.SetColour(note.Id, "PINK");
    book.SetBounds(note.Id, new Area(120, 340, 260, 210));
    var stamped = note.Updated;
    book.Save();

    var back = NoteBook.Load(path);
    Check("a note left on the desktop comes back to the desktop", back.OnDesktop().Count == 1);
    Check("on the same paper", back.Find(note.Id)?.Colour == "pink");
    Check("in the same place",
        Area.FromArray(back.Find(note.Id)?.Bounds) == new Area(120, 340, 260, 210));

    // Handling a note is not writing it: nudging a window must not shuffle the
    // list under the user.
    Check("moving and recolouring do not count as editing",
        back.Find(note.Id)?.Updated == stamped);

    book.SetStuck(note.Id, false);
    Check("taking it back off the desktop keeps the note", book.Count() == 1 && book.OnDesktop().Count == 0);

    // A blank sticky is a piece of paper with the caret on it, not a list row.
    var blank = book.Blank();
    book.SetStuck(blank.Id, true);
    Check("a blank sticky can be made", book.Count() == 2 && blank.Text.Length == 0);

    // The dashboard prunes on every repaint, and a new sticky is empty until
    // the first keystroke. Sweeping it up would delete the paper the user is
    // typing on, and every character after that would go nowhere.
    Check("a blank note on the desktop is not swept up", book.Prune() == 0 && book.Count() == 2);

    book.SetStuck(blank.Id, false);
    Check("but once it is off the desktop it is", book.Prune() == 1 && book.Count() == 1);

    File.WriteAllText(path,
        @"{ ""notes"": [ { ""id"": ""b"", ""text"": ""x"", ""colour"": ""neon"", ""bounds"": [1,2] } ] }");
    var odd = NoteBook.Load(path);
    Check("a hand-edited colour that isn't one is repaired", odd.Find("b")?.Colour == NoteColour.Default);
    Check("and half a rectangle is dropped, not half-used", odd.Find("b")?.Bounds is null);

    Directory.Delete(dir, recursive: true);
}

// --- first run without a text editor ------------------------------------------
//
// The old first run was: find %APPDATA%, write JSON by hand, know that "token"
// means a session token. Everyone who was not already a CLI user just had a
// mascot saying Unauthorized.

Check("a bare hostname is taken to mean https",
    CoderConfig.CleanUrl("coder.example.com") == "https://coder.example.com");
Check("a trailing slash is trimmed",
    CoderConfig.CleanUrl("https://coder.example.com/") == "https://coder.example.com");
Check("plaintext http to a real host is still refused",
    CoderConfig.CleanUrl("http://coder.example.com") is null);
Check("loopback http is allowed", CoderConfig.CleanUrl("http://127.0.0.1:4820") is not null);
Check("embedded userinfo is refused",
    CoderConfig.CleanUrl("https://coder.example.com@evil.com") is null);
Check("nothing is not a URL", CoderConfig.CleanUrl("   ") is null);
Check("a host with a port is still a host",
    CoderConfig.CleanUrl("coder.example.com:8443") == "https://coder.example.com:8443");

// The convenience above must not become a way past the scheme check: gluing
// https:// onto something that already names a scheme would accept exactly what
// the loader is tested to reject.
Check("a gadget scheme is not turned into a host", CoderConfig.CleanUrl("ms-msdt:/id") is null);
Check("nor is a file path", CoderConfig.CleanUrl(@"file://server/share") is null);

Check("the token page hangs off the address",
    CoderConfig.TokenPage("coder.example.com") == "https://coder.example.com/cli-auth");
Check("and there is no token page without one", CoderConfig.TokenPage("://") is null);

Check("a token pasted with a line break is refused", !CoderConfig.IsUsableToken("abc\ndef"));
Check("a smart quote from a chat window is refused", !CoderConfig.IsUsableToken("abc\u201cdef"));
Check("an ordinary token is fine", CoderConfig.IsUsableToken("  kZ8rQ2mN  "));

// The rule everywhere else is that a token is never written to disk. This is the
// one place it must be: the user typed it into our own window, so there is no
// other copy of it, and refusing to store it means asking again every launch.
Reset();
Environment.SetEnvironmentVariable("CODER_URL", null);
Environment.SetEnvironmentVariable("CODER_SESSION_TOKEN", null);

var fresh = CoderConfig.Load();
Check("with nothing to go on, setup is needed", fresh.NeedsSetup);
Check("a token that isn't usable is not adopted", !fresh.Adopt("https://x.io", "bad token"));
Check("nor is a URL that isn't", !fresh.Adopt("ftp://x.io", "GOODTOKEN"));

Check("what the user typed is adopted", fresh.Adopt("coder.example.com", " TYPED-BY-HAND "));
Check("and normalised on the way in",
    fresh.Url == "https://coder.example.com" && fresh.Token == "TYPED-BY-HAND");
Check("setup is no longer needed", !fresh.NeedsSetup);

fresh.Save();
Check("a token the user typed IS written down", Disk().Contains("TYPED-BY-HAND"));
Check("and read back on the next launch", CoderConfig.Load().Token == "TYPED-BY-HAND");

// Adopting must clear the borrowed flags, or the very next save strips what was
// just typed back out again.
Reset();
Environment.SetEnvironmentVariable("CODER_SESSION_TOKEN", "BORROWED-FROM-ENV");
var replacing = CoderConfig.Load();
Check("the borrowed token is in use", replacing.Token == "BORROWED-FROM-ENV");

replacing.Adopt("https://x.io", "TYPED-OVER-THE-TOP");
replacing.Save();
Check("typing one over the top replaces it on disk", Disk().Contains("TYPED-OVER-THE-TOP"));
Check("and the borrowed one is not written", !Disk().Contains("BORROWED-FROM-ENV"));
Environment.SetEnvironmentVariable("CODER_SESSION_TOKEN", null);

// --- what is on my ports ------------------------------------------------------
//
// The OS table lists the same server three or four times — IPv4, IPv6, extra
// binds. A port list that reports one Vite server four times is one nobody uses.

{
    (string Name, TimeSpan Uptime)? Named(int pid) => pid switch
    {
        100 => ("node", TimeSpan.FromMinutes(30)),
        200 => ("svchost", TimeSpan.FromHours(9)),
        300 => ("dotnet", TimeSpan.FromMinutes(5)),
        _ => null,
    };

    var rows = new[]
    {
        new RawListener(5173, 100, Loopback: true, AnyAddress: false),   // IPv4 127.0.0.1
        new RawListener(5173, 100, Loopback: true, AnyAddress: false),   // IPv6 ::1
        new RawListener(135, 200, Loopback: false, AnyAddress: true),
        new RawListener(5001, 300, Loopback: false, AnyAddress: true),
        new RawListener(7000, 999, Loopback: true, AnyAddress: false),   // process already gone
        new RawListener(70000, 100, Loopback: true, AnyAddress: false),  // not a port
    };

    var all = LocalPorts.Combine(rows, Named);
    Check("one server on one port is one row", all.Count(l => l.Port == 5173) == 1);
    Check("a port whose process is gone is not listed", all.All(l => l.Port != 7000));
    Check("an impossible port number is dropped", all.All(l => l.Port <= 65535));
    Check("sorted by port, because that is how it is read",
        all.Select(l => l.Port).SequenceEqual(all.Select(l => l.Port).OrderBy(p => p)));

    Check("a dev runtime is marked as yours",
        all.First(l => l.Port == 5173) is { Mine: true, Scope: PortScope.Loopback });
    Check("and the machine's own services are not",
        all.First(l => l.Port == 135) is { Mine: false, Scope: PortScope.AllInterfaces });
    Check("a port on all interfaces says so",
        all.First(l => l.Port == 5001).Where == "all interfaces");

    // Bound to both 127.0.0.1 and 0.0.0.0: the honest answer is the wider one,
    // because that is who can actually reach it.
    var both = LocalPorts.Combine(
        [new RawListener(8080, 100, true, false), new RawListener(8080, 100, false, true)], Named);
    Check("reachability is the widest bind, not the last one read",
        both.Single().Scope == PortScope.AllInterfaces);

    // Two processes really can hold the same port on different addresses, and
    // hiding one is how you spend an afternoon on a port you thought you freed.
    var shared = LocalPorts.Combine(
        [new RawListener(3000, 100, true, false), new RawListener(3000, 300, false, true)], Named);
    Check("the same port held by two processes is two facts", shared.Count == 2);

    Check("system ports are hidden by default",
        LocalPorts.Visible(all, null, includeSystem: false).All(l => l.Port >= 1024));
    Check("and shown when asked for",
        LocalPorts.Visible(all, null, includeSystem: true).Any(l => l.Port == 135));

    Check("a number searches ports", LocalPorts.Visible(all, "5173", false).Single().Port == 5173);
    Check("a partial number still finds it", LocalPorts.Visible(all, "51", false).Single().Port == 5173);
    Check("a word searches process names",
        LocalPorts.Visible(all, "NODE", false).Single().Process == "node");
    Check("a number does not match a pid", LocalPorts.Visible(all, "100", false).Count == 0);

    Check("the summary counts what is shown against everything",
        LocalPorts.Summarise(LocalPorts.Visible(all, null, false), all.Count).StartsWith("2 of 3"));
    Check("nothing listening says so", LocalPorts.Summarise([], 0) == "Nothing is listening.");

    Check("a port can be looked up directly", LocalPorts.On(all, 5001)?.Process == "dotnet");
    Check("and a free one answers nothing", LocalPorts.On(all, 4444) is null);
}

// --- the global shortcut ------------------------------------------------------
//
// A bare key here would be registered system-wide: bind "N" and every other
// application on the machine stops receiving the letter N.

Check("a plain shortcut parses", Hotkey.Parse("Ctrl+Alt+N")?.Text == "Ctrl+Alt+N");
Check("order typed does not change what is shown", Hotkey.Parse("alt+ctrl+n")?.Text == "Ctrl+Alt+N");
Check("function keys work", Hotkey.Parse("Ctrl+Shift+F9")?.Text == "Ctrl+Shift+F9");
Check("a modifier is required", Hotkey.Parse("N") is null);
Check("two keys is a typo, not a chord", Hotkey.Parse("Ctrl+N+M") is null);
Check("an unknown key name is refused", Hotkey.Parse("Ctrl+Banana") is null);
Check("blank means no shortcut at all", Hotkey.Parse("  ") is null);
Check("the default is one we accept", Hotkey.Parse(Hotkey.Default) is not null);

// --- starting with Windows ------------------------------------------------------
//
// Default-on: an app whose job is to notice the workspace dying is useless on
// the day you forgot to launch it.
Reset();
Check("startup is on unless it was turned off", CoderConfig.Load().StartWithWindows);

Reset();
WriteConfig(new { url = "https://x.io", startWithWindows = false });
Check("turning it off sticks", !CoderConfig.Load().StartWithWindows);

Reset();
WriteConfig(new { url = "https://x.io", workspaceIdleMinutes = -5, devServerIdleMinutes = 1 });
var idle = CoderConfig.Load();
Check("a negative idle window means off, not always-idle", idle.WorkspaceIdleMinutes == 0);
Check("a one-minute dev-server window is clamped up", idle.DevServerIdleMinutes >= 5);


// --- what have I merged where -------------------------------------------------
//
// This is the part that can be quietly wrong. A merge subject parsed to the
// wrong branch, or an ancestry set read the wrong way round, produces a screen
// that looks authoritative and lies — worse for someone relying on it than
// showing nothing. So the parsers are exercised against the exact shapes git,
// GitHub and GitLab actually print.

{
    string Row(params string[] fields) => string.Join(GitParse.Sep, fields);

    var refs = GitParse.Refs(string.Join('\n',
        Row("refs/heads/main", "a1b2c3d", "1755000000", "dev", "fix the cap"),
        Row("refs/remotes/origin/main", "a1b2c3d", "1755000000", "dev", "fix the cap"),
        Row("refs/heads/feature/bulk-usercc", "9f8e7d6", "1755600000", "dev", "batch add user cc"),
        Row("refs/remotes/origin/Golive-Redesign", "5c4b3a2", "1755300000", "haph", "menu golive"),
        Row("refs/remotes/origin/HEAD", "0000000", "0", "", ""),
        "malformed line with no separators"));

    Check("a branch on both sides is one row", refs.Count(b => b.Name == "main") == 1);
    Check("and is marked as being on both", refs.First(b => b.Name == "main").Side == RefSide.Both);
    Check("a local-only branch says so",
        refs.First(b => b.Name == "feature/bulk-usercc").Side == RefSide.Local);
    Check("a remote-only branch says so",
        refs.First(b => b.Name == "Golive-Redesign").Side == RefSide.Remote);
    Check("origin/HEAD is not a branch", refs.All(b => b.Name != "HEAD"));
    Check("a malformed line is skipped, not crashed on", refs.Count == 3);
    Check("newest first", refs[0].Name == "feature/bulk-usercc");
    Check("times are read as unix seconds", refs[0].Updated.ToUnixTimeSeconds() == 1755600000);

    // Merge subjects, as the three tools that write them actually write them.
    Check("git's own subject",
        GitParse.SourceOf("Merge branch 'feature/bulk-usercc'") == "feature/bulk-usercc");
    Check("git's into-form", GitParse.SourceOf("Merge branch 'hotfix/x' into main") == "hotfix/x");
    Check("GitLab's quoted target",
        GitParse.SourceOf("Merge branch 'Golive-Redesign' into 'main'") == "Golive-Redesign");
    Check("a remote-tracking merge drops the remote",
        GitParse.SourceOf("Merge remote-tracking branch 'origin/feature/x'") == "feature/x");
    Check("GitHub's pull request form",
        GitParse.SourceOf("Merge pull request #412 from acme/feature/bulk-users") == "feature/bulk-users");
    Check("a branch with slashes survives the owner strip",
        GitParse.SourceOf("Merge pull request #7 from org/release/2026-08") == "release/2026-08");
    Check("an ordinary commit is not a merge", GitParse.SourceOf("fix the cap") is null);
    Check("an unrecognised merge is left out rather than guessed",
        GitParse.SourceOf("Merged in something (pull request #9)") is null);

    var merges = GitParse.Merges("main", string.Join('\n',
        Row("aaa111", "1755600000", "dev", "Merge pull request #412 from acme/feature/bulk-users"),
        Row("bbb222", "1755000000", "haph", "Merge branch 'Golive-Redesign' into 'main'"),
        Row("ccc333", "1754000000", "dev", "just a commit")));

    Check("only merges become landings", merges.Count == 2);
    Check("newest landing first", merges[0].Source == "feature/bulk-users");
    Check("the landing carries its target", merges.All(m => m.Target == "main"));

    var merged = GitParse.Merged(string.Join('\n',
        "  refs/heads/main",
        "* refs/heads/Golive-Redesign",
        "  refs/remotes/origin/Golive-Redesign",
        "  refs/remotes/origin/HEAD -> origin/main"));

    Check("merged sets ignore the remote prefix", merged.Contains("Golive-Redesign"));
    Check("and the HEAD pointer", !merged.Any(n => n.Contains("HEAD")));

    var deployed = new Dictionary<string, string> { ["main"] = "production", ["Golive-Redesign"] = "staging" };
    var sets = new Dictionary<string, IReadOnlySet<string>?>
    {
        ["main"] = new HashSet<string> { "Golive-Redesign" },
        ["Golive-Redesign"] = new HashSet<string> { "feature/bulk-usercc" },
    };

    var standing = GitParse.Standing(refs, deployed, sets, merges);
    var feature = standing.First(b => b.Name == "feature/bulk-usercc");

    Check("deploy branches sort to the top", standing[0].IsTarget);
    Check("a branch merged to staging only is partly landed",
        feature.MergedSomewhere && !feature.MergedEverywhere);
    Check("the standing names the environment, not just the branch",
        feature.Standings.Any(s => s is { Environment: "staging", Merged: true }));
    Check("and says plainly where it has not landed",
        feature.Standings.Any(s => s is { Environment: "production", Merged: false }));
    Check("a deploy branch is not measured against itself",
        standing.First(b => b.Name == "main").Standings.All(s => s.Target != "main"));

    var golive = standing.First(b => b.Name == "Golive-Redesign");
    Check("a branch that landed on main knows when",
        golive.Standings.First(s => s.Target == "main").LandedAt?.ToUnixTimeSeconds() == 1755000000);
    Check("one that landed with no merge commit is still merged, just undated",
        feature.Standings.First(s => s.Target == "Golive-Redesign") is { Merged: true, LandedAt: null });

    // The cleanup case everybody forgets: merged everywhere, remote already gone.
    var localOnly = new BranchLine
    {
        Name = "old/thing", Sha = "1", Updated = DateTimeOffset.Now, Author = "x", Subject = "y",
        Side = RefSide.Local,
    };
    var swept = GitParse.Standing([localOnly], deployed,
        new Dictionary<string, IReadOnlySet<string>?>
        {
            ["main"] = new HashSet<string> { "old/thing" },
            ["Golive-Redesign"] = new HashSet<string> { "old/thing" },
        }, []);

    Check("a merged local branch with no remote is safe to delete", swept[0].SafeToDelete);
    Check("a deploy branch is never called safe to delete",
        !standing.First(b => b.IsTarget).SafeToDelete);

    var status = GitParse.Status(string.Join('\n',
        "# branch.oid a1b2c3d",
        "# branch.head Golive-Redesign",
        "1 .M N... 100644 100644 100644 aaa bbb src/x.cs",
        "? untracked.txt"));

    Check("the checked-out branch is read", status.Branch == "Golive-Redesign");
    Check("and uncommitted files are counted", status.Dirty == 2);
    Check("a detached head is not a branch name",
        GitParse.Status("# branch.head (detached)").Branch is null);

    Check("ahead-behind is read as two numbers", GitParse.AheadBehind("7 12") == (7, 12));
    Check("and refused when it isn't", GitParse.AheadBehind("") is null);
    Check("git 2.41 can count", GitParse.SupportsAheadBehind("git version 2.43.0.windows.1"));
    Check("git 2.39 cannot", !GitParse.SupportsAheadBehind("git version 2.39.2"));
    Check("git 3.0 could", GitParse.SupportsAheadBehind("git version 3.0.0"));
    Check("nonsense is not a version", !GitParse.SupportsAheadBehind("no git here"));
}

// A repository that can't be read must say why, in words, and never throw.
{
    var project = new GitProject { Name = "demo", Path = "/nope", Host = RepoHost.Workspace };

    var reader = new GitReader((_, _, _) =>
        Task.FromResult(GitOutput.Bad("fatal: not a git repository (or any of the parent directories)")));

    var snapshot = await reader.ReadAsync(project, CancellationToken.None);
    Check("an unreadable repo is reported, not thrown", !snapshot.Ok);
    Check("and the message names the path", snapshot.Problem!.Contains("/nope"));
}

// Never run a bare "git": CreateProcess searches this app's own folder before
// PATH, and the app ships as a portable folder people drop in Downloads.
{
    var dir = Directory.CreateTempSubdirectory("mascot-git").FullName;
    var fake = Path.Combine(dir, "git-stand-in");
    File.WriteAllText(fake, "");

    Reset();
    var cfg = CoderConfig.Load();

    cfg.GitPath = fake;
    Check("an absolute git path from the config is used", GitCli.Resolve(cfg) == fake);

    cfg.GitPath = "git";
    Check("a bare name in the config is refused, not searched for", GitCli.Resolve(cfg) is null);

    cfg.GitPath = Path.Combine(dir, "not-there");
    Check("a path to nothing is refused", GitCli.Resolve(cfg) is null);

    cfg.GitPath = fake;
    Check("correcting the config takes effect without a restart", GitCli.Resolve(cfg) == fake);

    Directory.Delete(dir, recursive: true);
}

// A project entry from config: the folder name stands in for a missing name, and
// a deploy branch with no environment named is its own environment.
{
    var entry = new ProjectEntry
    {
        Path = "/home/coder/workspace/projects/CONTRACT-APP/",
        Deployed = new Dictionary<string, string> { ["main"] = "", ["Golive-Redesign"] = "staging" },
    }.Tidy();

    Check("a project with no name takes the folder's", entry.Name == "CONTRACT-APP");
    Check("a deploy branch with no environment names itself", entry.Deployed!["main"] == "main");
    Check("workspace is the default home", entry.ToProject().Host == RepoHost.Workspace);
    Check("and local is honoured when asked for",
        new ProjectEntry { Path = "C:/src/x", Where = "LOCAL" }.Tidy().ToProject().Host == RepoHost.Local);
}


// --- the answers this window must never fake ----------------------------------
//
// Every case below was a real defect: a failed command rendering as "not
// merged", a clone whose remote isn't called origin reporting no branches at
// all, and a merge *out* of a deploy branch being counted as a merge into it.

{
    string Row(params string[] fields) => string.Join(GitParse.Sep, fields);

    var refTable = string.Join('\n',
        Row("refs/heads/main", "a1b2c3d", "1755000000", "dev", "fix"),
        Row("refs/remotes/upstream/main", "a1b2c3d", "1755100000", "dev", "fix"),
        Row("refs/heads/feature/x", "9f8e7d6", "1755600000", "dev", "work"));

    // A clone whose remote is called anything but origin.
    var asked = new List<string>();
    var reader = new GitReader((_, args, _) =>
    {
        var line = string.Join(' ', args);
        asked.Add(line);

        // --is-bare-repository, then --is-inside-work-tree: an ordinary clone.
        if (line.StartsWith("rev-parse", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("false\ntrue"));
        if (line.StartsWith("status", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("# branch.head main"));
        if (line.StartsWith("--version", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("git version 2.43.0"));
        if (line.StartsWith("for-each-ref", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good(refTable));
        if (line.StartsWith("branch", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("refs/heads/feature/x"));
        if (line.StartsWith("log", StringComparison.Ordinal)) return Task.FromResult(GitOutput.Good(""));

        return Task.FromResult(GitOutput.Bad("unexpected: " + line));
    });

    var project = new GitProject
    {
        Name = "demo",
        Path = "/repo",
        Host = RepoHost.Local,
        Deployed = new Dictionary<string, string> { ["main"] = "production" },
    };

    var snap = await reader.ReadAsync(project, CancellationToken.None);
    Check("a repository whose remote isn't 'origin' still reads", snap.Ok && snap.Branches.Count == 2);
    Check("and is measured against the ref that actually exists",
        asked.Any(a => a.Contains("refs/remotes/upstream/main")));
    Check("never against a rebuilt origin ref",
        !asked.Any(a => a.Contains("refs/remotes/origin/")));
    Check("the branch that has landed says so",
        snap.Branches.First(b => b.Name == "feature/x").Standings[0].State == Landed.Yes);

    // The merged-set command fails. "We could not find out" must not render as
    // "not merged" — that reads as an answer and is indistinguishable from one.
    var blind = new GitReader((_, args, _) =>
    {
        var line = string.Join(' ', args);

        // --is-bare-repository, then --is-inside-work-tree: an ordinary clone.
        if (line.StartsWith("rev-parse", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("false\ntrue"));
        if (line.StartsWith("status", StringComparison.Ordinal)) return Task.FromResult(GitOutput.Good(""));
        if (line.StartsWith("--version", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("git version 2.30.0"));
        if (line.StartsWith("for-each-ref", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good(refTable));
        if (line.StartsWith("branch", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Bad("error: connection closed"));

        return Task.FromResult(GitOutput.Good(""));
    });

    var unsure = await blind.ReadAsync(project, CancellationToken.None);
    var unknown = unsure.Branches.First(b => b.Name == "feature/x");

    Check("a failed merged-check reads as unknown, not as 'not merged'",
        unknown.Standings[0].State == Landed.Unknown);
    Check("an unknown is not counted as landed everywhere", !unknown.MergedEverywhere);
    Check("and the reading says out loud that it couldn't check",
        unsure.Warning is { Length: > 0 } && unsure.Ok);
    Check("the pill says unknown in words", unknown.Standings[0].Label.Contains("unknown"));

    // An old git can't do %(ahead-behind:) — that costs the numbers, never the list.
    Check("an old git still lists branches", unsure.Branches.Count == 2);
    Check("just without counts", unsure.Branches.All(b => b.Ahead is null));

    // A ref read that fails outright is a real problem, not a silent empty list.
    var broken = new GitReader((_, args, _) =>
    {
        var line = string.Join(' ', args);
        // --is-bare-repository, then --is-inside-work-tree: an ordinary clone.
        if (line.StartsWith("rev-parse", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("false\ntrue"));
        if (line.StartsWith("for-each-ref", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Bad("fatal: broken index"));

        return Task.FromResult(GitOutput.Good(""));
    });

    Check("an unreadable ref table is reported",
        !(await broken.ReadAsync(project, CancellationToken.None)).Ok);
}

// A merge *out* of a deploy branch sits on that branch's spine but is not a
// landing on it — counting it produces a nonsense "main → main" row and steals
// the landing date from the branch that really did arrive.
{
    string Row(params string[] fields) => string.Join(GitParse.Sep, fields);

    Check("the into-branch is read back",
        GitParse.Merge("Merge branch 'main' into feature/x").Into == "feature/x");
    Check("GitLab's quoted into-branch too",
        GitParse.Merge("Merge branch 'feature/x' into 'main'").Into == "main");
    Check("a merge with no into-clause has none",
        GitParse.Merge("Merge branch 'feature/x'").Into is null);

    var spine = GitParse.Merges("main", string.Join('\n',
        Row("aaa", "1755600000", "dev", "Merge branch 'feature/x' into 'main'"),
        Row("bbb", "1755500000", "dev", "Merge branch 'main' into feature/x"),
        Row("ccc", "1755400000", "dev", "Merge branch 'hotfix/y'")));

    Check("a merge into this branch counts", spine.Any(m => m.Source == "feature/x"));
    Check("a merge with no into-clause counts", spine.Any(m => m.Source == "hotfix/y"));
    Check("a merge out of this branch does not", !spine.Any(m => m.Source == "main"));
    Check("so nothing claims a branch merged into itself", spine.All(m => m.Source != m.Target));
}

// Dedupe keeps the newer commit, and ahead/behind survives the merge of the two
// sides — a wrong field index or a lost ?? would otherwise pass unnoticed.
{
    string Row(params string[] fields) => string.Join(GitParse.Sep, fields);

    var refs = GitParse.Refs(string.Join('\n',
        Row("refs/heads/main", "old1234", "1755000000", "dev", "older local commit"),
        Row("refs/remotes/origin/main", "new5678", "1755900000", "haph", "newer remote commit", "3 12")));

    var main = refs.Single();
    Check("the newer side wins the dedupe", main.Sha == "new5678" && main.Author == "haph");
    Check("and the branch is still known to be on both", main.Side == RefSide.Both);
    Check("ahead/behind survives the merge", main is { Ahead: 3, Behind: 12 });
    Check("the remote ref is remembered verbatim", main.RemoteRef == "refs/remotes/origin/main");

    var localNewer = GitParse.Refs(string.Join('\n',
        Row("refs/heads/main", "loc1", "1755900000", "dev", "newer local"),
        Row("refs/remotes/origin/main", "rem1", "1755000000", "haph", "older remote", "0 0")));

    Check("dedupe follows the timestamps, not the order read",
        localNewer.Single().Sha == "loc1");
    Check("and still keeps the remote ref for measuring against",
        localNewer.Single().RemoteRef == "refs/remotes/origin/main");
}

// Two checkouts of the same repository take the same folder name; identity has
// to survive that, and the same path twice is one project.
{
    Reset();
    WriteConfig(new
    {
        url = "https://x.io",
        projects = new[]
        {
            new { name = (string?)null, path = "/home/coder/workspace/projects/api", where = "workspace" },
            new { name = (string?)null, path = "/home/coder/share-projects/api", where = "workspace" },
            new { name = (string?)null, path = "/home/coder/workspace/projects/api", where = "workspace" },
        },
    });

    var projects = CoderConfig.Load().Projects.Select(p => p.ToProject()).ToList();
    Check("the same path twice is one project", projects.Count == 2);
    Check("two folders of the same name both survive", projects.All(p => p.Name == "api"));
    Check("and are told apart by identity, not name",
        projects[0].Id != projects[1].Id);
    Check("a repo with no deploy branches has nothing outstanding",
        new ProjectSnapshot { Name = "x", Path = "/x" }.Outstanding == 0);
}

// --- The repository editor: what it may and may not write to the config ------
//
// The whole point of the form is that a path and a branch name typed by hand
// fail *quietly* — a wrong path reads as "doesn't exist", a wrong branch name
// reads as "not merged" for something that shipped weeks ago. So the rules that
// decide what can be saved live in ProjectDraft, out of WPF's reach, and are
// checked here rather than by clicking.

{
    var draft = ProjectDraft.Blank(RepoHost.Workspace);

    Check("a blank draft can't be saved", draft.Problem is { Length: > 0 });
    Check("and says the folder is what's missing",
        draft.Problem!.Contains("folder", StringComparison.OrdinalIgnoreCase));

    draft.Path = "/home/coder/workspace/projects/contract";
    Check("a workspace path is accepted", draft.Problem is null);
    Check("the folder names the project", draft.FolderName() == "contract");

    // A Windows path handed to `coder ssh` comes back as "doesn't exist in the
    // workspace", which reads as a missing folder rather than the wrong kind of
    // path — the one mistake this form exists to catch before it is made.
    draft.Path = @"C:\src\contract";
    Check("a Windows path in the workspace is refused", draft.Problem is { Length: > 0 });
    Check("and the message says which way to go",
        draft.Problem!.Contains("Windows path", StringComparison.Ordinal));

    draft.Path = @"\\server\share\repo";
    Check("a UNC path in the workspace is refused too", draft.Problem is { Length: > 0 });

    draft.Host = RepoHost.Local;
    Check("the same Windows path is fine on this PC", draft.Problem is null);

    draft.Path = "/home/coder/workspace/projects/contract";
    Check("and a Linux path on this PC is refused", draft.Problem is { Length: > 0 });
    Check("naming the other setting", draft.Problem!.Contains("workspace", StringComparison.Ordinal));
}

// Deploy branches: added, deduplicated, reordered, and never silently dropped.
{
    var draft = ProjectDraft.Blank(RepoHost.Workspace);
    draft.Path = "/home/coder/workspace/projects/api";

    Check("a repo with no deploy branches is still saveable", draft.Problem is null);
    Check("but is told what it can't answer", draft.Advice is { Length: > 0 });

    Check("a blank branch is not added", !draft.Add("   ", "production"));
    Check("a real one is", draft.Add("main", null));
    Check("no advice once there is one", draft.Advice is null);

    Check("the same branch twice is refused", !draft.Add("main", "staging"));
    Check("case matters, because it does to git", draft.Add("Main", null));
    Check("a second branch is added", draft.Add("Golive-Redesign", "staging"));

    Check("a blank environment falls back to a suggestion",
        draft.Deploys[0].Environment == "production");
    Check("and the suggestion doesn't care about the case of the branch",
        draft.Deploys[1].Environment == "production");
    Check("a given environment is kept verbatim",
        draft.Deploys[2].Environment == "staging");

    // Which branch the ahead/behind counts run against is the top of the list,
    // and it has to survive the trip through the file.
    var entry = draft.ToEntry();
    Check("the first deploy branch is written down as primary", entry.Primary == "main");

    draft.MakePrimary(draft.Deploys[2]);
    Check("promoting one moves it to the top", draft.Deploys[0].Branch == "Golive-Redesign");
    Check("and the entry follows", draft.ToEntry().Primary == "Golive-Redesign");
    Check("without losing the others", draft.ToEntry().Deployed!.Count == 3);

    draft.Remove(draft.Deploys[0]);
    Check("removing the primary hands the job to the next one",
        draft.ToEntry().Primary == "main");
}

// A saved project comes back the same shape it went in.
{
    var draft = ProjectDraft.Blank(RepoHost.Local);
    draft.Path = @"C:\src\admin-hub";
    draft.Name = "Admin Hub";
    draft.Add("main", "production");
    draft.Add("develop", null);

    var reopened = ProjectDraft.From(draft.ToEntry());

    Check("the folder survives the round trip", reopened.Path == @"C:\src\admin-hub");
    Check("so does the name", reopened.Name == "Admin Hub");
    Check("and where it lives", reopened.Host == RepoHost.Local);
    Check("both deploy branches come back", reopened.Deploys.Count == 2);
    Check("the primary still leads", reopened.Deploys[0].Branch == "main");
    Check("with its environment", reopened.Deploys[0].Environment == "production");
    Check("and the suggested one", reopened.Deploys[1].Environment == "development");
    Check("editing an existing project knows what it replaces",
        reopened.Replacing == "Local:C:\\src\\admin-hub" && !reopened.IsNew);
}

// A primary naming a branch nobody deploys from is worse than none: it reads as
// configured while measuring nothing.
{
    var entry = new ProjectEntry
    {
        Path = "/repo",
        Where = "workspace",
        Deployed = new Dictionary<string, string> { ["main"] = "production" },
        Primary = "gone",
    }.Tidy();

    Check("a primary that isn't a deploy branch is dropped", entry.Primary is null);
    Check("and the count falls back to the first target",
        entry.ToProject().PrimaryTarget == "main");

    var named = new ProjectEntry
    {
        Path = "/repo",
        Deployed = new Dictionary<string, string> { ["main"] = "production", ["stage"] = "staging" },
        Primary = "stage",
    }.Tidy().ToProject();

    Check("a named primary wins over first-in-the-file", named.PrimaryTarget == "stage");
}

// "where" is canonicalised, because ToProject() reads anything that isn't
// "local" as a workspace repo — so a null and a "workspace" are one project, and
// a list deduplicated on the raw string would keep both under one identity.
{
    var entries = CoderConfig.NormalizeProjects(
    [
        new ProjectEntry { Path = "/repo/api" },
        new ProjectEntry { Path = "/repo/api", Where = "workspace" },
        new ProjectEntry { Path = "/repo/api", Where = "WORKSPACE" },
        new ProjectEntry { Path = "   " },
        new ProjectEntry { Path = "/repo/web", Where = "LOCAL" },
        null,
    ]);

    Check("three spellings of the same workspace repo are one project", entries.Length == 2);
    Check("a blank path is not a project", entries.All(e => e.Path!.Trim().Length > 0));
    Check("where is written one way", entries[0].Where == "workspace");
    Check("both ways", entries[1].Where == "local");
    Check("and identity no longer depends on the spelling",
        entries[0].ToProject().Id == "Workspace:/repo/api");
}

// The editor writes through the same sieve as the file, and what it writes is
// what a restart reads back.
{
    Reset();
    Environment.SetEnvironmentVariable("CODER_URL", null);
    Environment.SetEnvironmentVariable("CODER_SESSION_TOKEN", null);
    WriteConfig(new { url = "https://coder.example.com", token = "USER-OWN-TOKEN" });

    var cfg = CoderConfig.Load();
    Check("nothing is watched to begin with", cfg.Projects.Length == 0);

    var draft = ProjectDraft.Blank(RepoHost.Workspace);
    draft.Path = "/home/coder/workspace/projects/contract";
    draft.Name = "CONTRACT-APP";
    draft.Add("Golive-Redesign", "staging");
    draft.Add("main", "production");

    cfg.SetProjects([draft.ToEntry(), new ProjectEntry { Path = "  " }]);
    cfg.Save();

    var reloaded = CoderConfig.Load();
    Check("the repository is on disk", reloaded.Projects.Length == 1);

    var project = reloaded.Projects[0].ToProject();
    Check("with its name", project.Name == "CONTRACT-APP");
    Check("its path", project.Path == "/home/coder/workspace/projects/contract");
    Check("both deploy branches", project.Deployed.Count == 2);
    Check("what they mean", project.Deployed["Golive-Redesign"] == "staging");
    Check("and which one the counts run against",
        project.PrimaryTarget == "Golive-Redesign");
    Check("saving projects doesn't disturb the token", Disk().Contains("USER-OWN-TOKEN"));

    // Removing the last one has to actually empty the file, not leave the old
    // list behind because an empty array looked like "nothing to write".
    reloaded.SetProjects([]);
    reloaded.Save();
    Check("removing the last repository empties the list",
        CoderConfig.Load().Projects.Length == 0);
}

Check("main is production", Environments.Suggest("main") == "production");
Check("master too", Environments.Suggest("MASTER") == "production");
Check("develop is development", Environments.Suggest("develop") == "development");
Check("stage is staging", Environments.Suggest("stage") == "staging");
Check("anything else is only ever itself",
    Environments.Suggest("Golive-AcmeSign") == "Golive-AcmeSign");

// The check behind the folder box. It exists to answer the two things the form
// cannot know on its own, and to be honest when it couldn't find out either.
{
    string Row(params string[] fields) => string.Join(GitParse.Sep, fields);

    var table = string.Join('\n',
        Row("refs/heads/main", "a1b2c3d", "1755000000", "dev", "fix"),
        Row("refs/remotes/origin/main", "a1b2c3d", "1755100000", "dev", "fix"),
        Row("refs/heads/Golive-Redesign", "9f8e7d6", "1755600000", "haph", "menu"));

    var project = new GitProject { Name = "x", Path = "/repo", Host = RepoHost.Workspace };

    var good = new GitReader((_, args, _) =>
    {
        var line = string.Join(' ', args);
        // --is-bare-repository, then --is-inside-work-tree: an ordinary clone.
        if (line.StartsWith("rev-parse", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("false\ntrue"));
        if (line.StartsWith("for-each-ref", StringComparison.Ordinal)) return Task.FromResult(GitOutput.Good(table));
        if (line.StartsWith("status", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("# branch.head main\n1 .M N... x\n1 .M N... y"));

        return Task.FromResult(GitOutput.Bad("unexpected: " + line));
    });

    var report = await good.ProbeAsync(project, CancellationToken.None);

    Check("a real repository checks out", report.Ok);
    Check("its branches are offered, deduplicated", report.Branches.Count == 2);
    Check("in an order somebody can scan",
        report.Branches[0] == "Golive-Redesign" && report.Branches[1] == "main");
    Check("the branch it is sitting on is known", report.CurrentBranch == "main");
    Check("so is the uncommitted work", report.DirtyFiles == 2);
    Check("and the summary says all of it",
        report.Summary.Contains("2 branches") && report.Summary.Contains("on main")
        && report.Summary.Contains("2 uncommitted"));

    // A folder that isn't a checkout, said in words the form can show verbatim.
    var missing = new GitReader((_, _, _) =>
        Task.FromResult(GitOutput.Bad("fatal: not a git repository (or any of the parent directories)")));

    var notARepo = await missing.ProbeAsync(project, CancellationToken.None);
    Check("a folder that isn't a repository is refused", !notARepo.Ok);
    Check("in a sentence, not a git error",
        notARepo.Problem!.Contains("isn't a git repository", StringComparison.Ordinal));

    var absent = new GitReader((_, _, _) =>
        Task.FromResult(GitOutput.Bad("fatal: cannot change to '/nope': No such file or directory")));

    var gone = await absent.ProbeAsync(project, CancellationToken.None);
    Check("a missing folder says where it was looked for",
        gone.Problem!.Contains("in the workspace", StringComparison.Ordinal));

    // `status` is decoration. Losing it must not cost the branch list, which is
    // the only thing the form actually needs from the check.
    var quiet = new GitReader((_, args, _) =>
    {
        var line = string.Join(' ', args);
        // --is-bare-repository, then --is-inside-work-tree: an ordinary clone.
        if (line.StartsWith("rev-parse", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("false\ntrue"));
        if (line.StartsWith("for-each-ref", StringComparison.Ordinal)) return Task.FromResult(GitOutput.Good(table));

        return Task.FromResult(GitOutput.Bad("status failed"));
    });

    var partial = await quiet.ProbeAsync(project, CancellationToken.None);
    Check("a failed status still leaves a usable check", partial.Ok && partial.Branches.Count == 2);
    Check("it just doesn't claim a current branch", partial.CurrentBranch is null);

    var blankPath = await good.ProbeAsync(
        new GitProject { Name = "x", Path = "  " }, CancellationToken.None);
    Check("an empty path is refused without running anything", !blankPath.Ok);
}

// --- Reading a repository from its URL --------------------------------------
//
// This is the only string in the app that becomes the address of something git
// will connect to, so what may be typed here is an allowlist. Two of the forms
// git itself accepts are dangerous rather than merely wrong:
//
//   ext::sh -c …   git's transport-helper syntax runs a shell command
//   --upload-pack= a leading dash is an option wherever the string lands
//
// Nothing below may start passing.

{
    (string Url, bool Accept, string Why)[] remotes =
    [
        ("https://git.example.com/team/contract-ui.git", true, "the ordinary case"),
        ("git@git.example.com:team/contract-ui.git", true, "scp-style, which is what GitLab shows you"),
        ("ssh://git@git.example.com:2222/team/app.git", true, "ssh with a port"),
        ("http://127.0.0.1:3000/x.git", true, "loopback http, which goes nowhere near a network"),

        ("ext::sh -c curl", false, "git runs this as a command"),
        ("ext::sh", false, "and does so with no arguments too"),
        ("fd::17/foo", false, "every other transport helper as well"),
        ("--upload-pack=/bin/sh", false, "a leading dash is an option, not an address"),
        ("-u", false, "however short"),
        ("http://git.example.com/x.git", false, "cleartext across a network"),
        ("git://git.example.com/x.git", false, "unauthenticated and unencrypted"),
        ("file:///C:/Windows/System32", false, "not a remote at all"),
        (@"C:\src\app", false, "a Windows path parses as scp-style host \"C\""),
        (@"\\server\share\repo", false, "a UNC path is not a URL"),
        ("https://git.example.com/x.git\nrm -rf", false, "a line break"),
        ("https://git.example.com/a b.git", false, "a space"),
        ("", false, "nothing"),
        ("   ", false, "nothing but whitespace"),
        ("contract-ui", false, "a bare name is not an address"),
    ];

    foreach (var (url, accept, why) in remotes)
    {
        var seen = GitUrl.Inspect(url);
        Check($"{(accept ? "accepts" : "refuses")} {url.Replace("\n", "\\n")} — {why}",
            (seen.Url is not null) == accept);

        if (!accept) Check("  ...with a reason", seen.Problem is { Length: > 0 });
    }

    Check("an accepted URL comes back unchanged",
        GitUrl.Clean("https://git.example.com/team/contract-ui.git")
            == "https://git.example.com/team/contract-ui.git");
    Check("and is trimmed of stray whitespace",
        GitUrl.Clean("  https://git.example.com/x.git  ") == "https://git.example.com/x.git");
}

// A password pasted along with the URL is removed rather than written to a file
// documented as plain settings — the same rule that keeps the Coder CLI's
// session token out of config.json.
{
    var withSecret = GitUrl.Inspect("https://dev:glpat-SECRET@git.example.com/team/app.git");

    Check("a URL carrying a password is still usable", withSecret.Url is { Length: > 0 });
    Check("but the password is gone",
        !withSecret.Url!.Contains("SECRET", StringComparison.Ordinal));
    Check("and it says so", withSecret.StrippedSignIn);
    Check("the username is kept, being no secret",
        withSecret.Url == "https://dev@git.example.com/team/app.git");

    var sshUser = GitUrl.Inspect("ssh://git@git.example.com/team/app.git");
    Check("a bare ssh username is not treated as a secret", !sshUser.StrippedSignIn);
    Check("and survives", sshUser.Url == "ssh://git@git.example.com/team/app.git");

    // Not stripped — refused. git reads the first colon as the host separator,
    // so this form means nothing to git anyway, and "fixing" it up would be
    // guessing at what somebody meant while holding their password.
    var scpSecret = GitUrl.Inspect("dev:glpat-SECRET@git.example.com:team/app.git");
    Check("a password in scp-style syntax is refused outright", scpSecret.Url is null);
    Check("and never comes back out of the checker",
        scpSecret.Problem is { Length: > 0 } && !scpSecret.Problem.Contains("SECRET", StringComparison.Ordinal));

    // And the config file is where it must never end up.
    var stored = new ProjectEntry
    {
        Path = "https://dev:glpat-SECRET@git.example.com/team/app.git",
        Where = "remote",
    }.Tidy();

    Check("nor does one reach the stored project",
        !stored.Path!.Contains("SECRET", StringComparison.Ordinal));
    Check("which still names the right repository", stored.Name == "app");
}

// Where the downloaded copy goes: readable, and unique per URL.
{
    Check("the name comes off the end of the URL",
        GitUrl.Slug("https://git.example.com/team/contract-ui.git") == "contract-ui");
    Check("with .git taken off", GitUrl.Slug("git@host:team/api.git") == "api");
    Check("a trailing slash doesn't confuse it", GitUrl.Slug("https://host/team/web/") == "web");
    Check("and something unusable still gets a name", GitUrl.Slug("https://host/") == "host");

    var a = GitUrl.Folder("https://git.example.com/team/api.git");
    var b = GitUrl.Folder("https://github.com/someone/api.git");

    Check("the folder is the same every time", a == GitUrl.Folder("https://git.example.com/team/api.git"));
    Check("two servers' \"api\" do not share one copy", a != b);
    Check("both are still readable", a.StartsWith("api-", StringComparison.Ordinal));
    Check("and safe as a folder name",
        a.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));
}

// The draft, told it is a URL, judges it as one.
{
    var draft = ProjectDraft.Blank(RepoHost.Remote);
    draft.Path = "https://git.example.com/team/contract-ui.git";

    Check("a URL repository can be saved", draft.Problem is null);
    Check("and names itself after the repository", draft.FolderName() == "contract-ui");

    draft.Path = "ext::sh -c evil";
    Check("a dangerous one cannot", draft.Problem is { Length: > 0 });

    draft.Path = "/home/coder/workspace/projects/app";
    Check("nor can a path pretending to be a URL", draft.Problem is { Length: > 0 });

    draft.Path = "https://git.example.com/team/contract-ui.git";
    draft.Add("main", "production");

    var entry = draft.ToEntry();
    Check("it is written down as a remote", entry.Where == "remote");
    Check("with the URL as its path", entry.Path == "https://git.example.com/team/contract-ui.git");

    var project = entry.ToProject();
    Check("and reads back as one", project.Host == RepoHost.Remote);
    Check("with its own identity", project.Id.StartsWith("Remote:", StringComparison.Ordinal));

    var reopened = ProjectDraft.From(entry);
    Check("re-opening it keeps the host", reopened.Host == RepoHost.Remote);
    Check("and the deploy branch", reopened.Deploys.Single().Branch == "main");

    // Older spellings anyone might have written by hand.
    Check("\"url\" means the same thing",
        new ProjectEntry { Path = "https://h/x.git", Where = "url" }.Tidy().ToProject().Host == RepoHost.Remote);
    Check("so does \"git\"",
        new ProjectEntry { Path = "https://h/x.git", Where = "GIT" }.Tidy().Where == "remote");
    Check("and anything unrecognised is still the workspace",
        new ProjectEntry { Path = "/x", Where = "elsewhere" }.Tidy().ToProject().Host == RepoHost.Workspace);
}

// A downloaded copy is bare, and a bare repository answers "no" to the work-tree
// question every time. Reading that as "not a repository" would break the whole
// feature, so both answers are asked for and either one is enough.
{
    Check("a bare repository is a repository", GitParse.Kind("true\nfalse") == (true, true));
    Check("so is an ordinary one", GitParse.Kind("false\ntrue") == (true, false));
    Check("something that is neither is not", GitParse.Kind("false\nfalse") == (false, false));
    Check("and nothing at all certainly isn't", GitParse.Kind("") == (false, false));
}

// The whole read, against a copy shaped exactly like the one the app fetches:
// bare, with the server's branches under refs/remotes/origin/*.
{
    string Row(params string[] fields) => string.Join(GitParse.Sep, fields);

    var table = string.Join('\n',
        Row("refs/remotes/origin/main", "daa7d5b", "1755000000", "dev", "Merge branch 'feature/landed' into 'main'"),
        Row("refs/remotes/origin/feature/landed", "f1d337e", "1754000000", "dev", "landed work"),
        Row("refs/remotes/origin/feature/open", "31ef46e", "1755600000", "haph", "open work"));

    var asked = new List<string>();

    var reader = new GitReader((_, args, _) =>
    {
        var line = string.Join(' ', args);
        asked.Add(line);

        // A bare copy: bare yes, work tree no. Exactly what git prints.
        if (line.StartsWith("rev-parse", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("true\nfalse"));

        if (line.StartsWith("--version", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good("git version 2.43.0"));

        if (line.StartsWith("for-each-ref", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good(table));

        if (line.StartsWith("branch", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good(
                "refs/remotes/origin/feature/landed\nrefs/remotes/origin/main"));

        if (line.StartsWith("log", StringComparison.Ordinal))
            return Task.FromResult(GitOutput.Good(
                Row("daa7d5b", "1755000000", "dev", "Merge branch 'feature/landed' into 'main'")));

        // `status` has no meaning here and fails for real. Reaching this is the
        // failure the test exists for.
        return Task.FromResult(GitOutput.Bad("fatal: this operation must be run in a work tree"));
    });

    var project = new GitProject
    {
        Name = "contract-ui",
        Path = "https://git.example.com/team/contract-ui.git",
        Host = RepoHost.Remote,
        Deployed = new Dictionary<string, string> { ["main"] = "production" },
    };

    var snap = await reader.ReadAsync(project, CancellationToken.None);

    Check("a downloaded copy reads", snap.Ok);
    Check("a bare copy is never asked about uncommitted work",
        !asked.Any(a => a.StartsWith("status", StringComparison.Ordinal)));
    Check("so nothing claims a current branch", snap.CurrentBranch is null && snap.DirtyFiles == 0);
    Check("every branch is there", snap.Branches.Count == 3);

    var landed = snap.Branches.First(b => b.Name == "feature/landed");
    var open = snap.Branches.First(b => b.Name == "feature/open");

    Check("what shipped says so", landed.Standings[0].State == Landed.Yes);
    Check("what hasn't says that", open.Standings[0].State == Landed.No);
    Check("with the date it landed", landed.Standings[0].LandedAt is not null);

    // Nothing here is a local branch, so nothing here is deletable — the copy is
    // the app's, and offering to tidy up someone else's server is nonsense.
    Check("no branch in a downloaded copy is offered for deletion",
        snap.Branches.All(b => !b.SafeToDelete));
    Check("because they are all the server's", snap.Branches.All(b => b.Side == RefSide.Remote));

    Check("and it is measured against the ref that exists",
        asked.Any(a => a.Contains("refs/remotes/origin/main", StringComparison.Ordinal)));

    // The same shape through the editor's check.
    var report = await reader.ProbeAsync(project, CancellationToken.None);
    Check("the editor's check works on a bare copy too", report.Ok);
    Check("and offers its branches", report.Branches.Count == 3);
    Check("without claiming a branch is checked out", report.CurrentBranch is null);
}

// Before it has been downloaded there is nothing to read, and that is said in
// words that name the button which fixes it.
{
    var never = new GitReader((_, _, _) => Task.FromResult(GitOutput.Bad(
        "This repository hasn't been downloaded yet. Press \u201cFetch from origin\u201d to fetch it once.")));

    var snap = await never.ReadAsync(
        new GitProject { Name = "x", Path = "https://h/x.git", Host = RepoHost.Remote },
        CancellationToken.None);

    Check("an undownloaded repository is a problem, not an empty list", !snap.Ok);
    Check("and the problem names the way out",
        snap.Problem!.Contains("Fetch from origin", StringComparison.Ordinal));
}

// The two format spellings are not interchangeable, and getting it wrong is
// silent: git exits 0 and prints the escape as text, so every field runs into
// the next and the repository reports no branches. See GitParse.RefFormat.
Check("for-each-ref gets a ref format",
    GitParse.RefFormat.Contains("%1f", StringComparison.Ordinal)
    && !GitParse.RefFormat.Contains("%x1f", StringComparison.Ordinal));
Check("including the ahead/behind variant",
    !GitParse.RefFormatWithCounts("refs/heads/main").Contains("%x1f", StringComparison.Ordinal));
Check("and git log gets a pretty one",
    GitParse.LogFormat.Contains("%x1f", StringComparison.Ordinal));

// --- Signing in to a private server ----------------------------------------
//
// The secret goes to Git's own credential store and never to this app's config.
// What is checked here is the protocol it travels over, which is line-based
// key=value on stdin — so a newline inside a value would start a new key, and a
// pasted password could set any other field in the request.

{
    var payload = GitSignIn.Payload("https://git.example.com/root/app.git", "dev", "glpat-abc123");

    Check("a sign-in becomes a credential request", payload is { Length: > 0 });
    Check("addressed by url, so git splits it the same way the fetch will",
        payload!.Contains("url=https://git.example.com/root/app.git\n", StringComparison.Ordinal));
    Check("carrying the username", payload.Contains("username=dev\n", StringComparison.Ordinal));
    Check("and the secret", payload.Contains("password=glpat-abc123\n", StringComparison.Ordinal));
    Check("terminated by the blank line git waits for",
        payload.EndsWith("\n\n", StringComparison.Ordinal));

    Check("a token alone is enough — some servers want no username",
        GitSignIn.Payload("https://h/x.git", "   ", "tok") is { } bare
        && !bare.Contains("username=", StringComparison.Ordinal));

    // Each of these would otherwise inject a second key into the request.
    Check("a newline in the token is refused",
        GitSignIn.Payload("https://h/x.git", "u", "tok\npassword=other") is null);
    Check("a newline in the username is refused",
        GitSignIn.Payload("https://h/x.git", "u\nhost=evil.com", "tok") is null);
    Check("a carriage return counts too",
        GitSignIn.Payload("https://h/x.git", "u", "tok\rmore") is null);
    Check("and so does any other control character",
        GitSignIn.Payload("https://h/x.git", "u", "tok\u0001more") is null);

    Check("no url, no request", GitSignIn.Payload("", "u", "t") is null);
    Check("no secret, no request", GitSignIn.Payload("https://h/x.git", "u", "") is null);
}

// Telling "the server wants to know who you are" apart from every other
// failure, because it is the only one with an answer on screen.
{
    // Verbatim from a real refused fetch on this machine.
    Check("git's own words for it",
        GitSignIn.Needed("fatal: could not read Username for 'https://git.example.com': terminal prompts disabled"));
    Check("a rejected password", GitSignIn.Needed("remote: HTTP Basic: Access denied"));
    Check("an outright 401", GitSignIn.Needed("The requested URL returned error: 401"));
    Check("a 403", GitSignIn.Needed("fatal: unable to access '...': The requested URL returned error: 403"));
    Check("GitLab's answer for a repo you can't see",
        GitSignIn.Needed("remote: Repository not found"));
    Check("an ssh key that isn't trusted",
        GitSignIn.Needed("git@host: Permission denied (publickey)."));

    Check("but not a network failure",
        !GitSignIn.Needed("fatal: unable to access 'https://h/x.git/': Could not resolve host: h"));
    Check("nor a broken repository", !GitSignIn.Needed("fatal: bad object HEAD"));
    Check("nor nothing at all", !GitSignIn.Needed(null) && !GitSignIn.Needed(""));

    Check("and there is a sentence to show instead of git's",
        GitSignIn.Ask.Contains("username", StringComparison.OrdinalIgnoreCase));
    Check("plus one for having nowhere to keep it",
        GitSignIn.NoStore.Contains("credential.helper", StringComparison.Ordinal));
}

// --- The defect this whole section exists for -------------------------------
//
// A downloaded copy that never actually downloaded. `git init --bare` creates
// the object store before any fetch, and a fetch that dies asking for a
// password writes FETCH_HEAD regardless — so both of git's own traces say
// "fetched" when nothing was. Believing either is what reported a private
// repository, in green, as having no branches.
{
    var url = "https://git.example.com/root/api-signserver.example.net.git";
    var dir = MirrorStore.PathFor(url);

    try
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        Check("nothing downloaded, nothing claimed", !MirrorStore.Fetched(url));
        Check("and it says so rather than reading as empty", MirrorStore.NotReady(url) is { Length: > 0 });

        // Everything `git init --bare` leaves behind, and everything a refused
        // fetch leaves behind, together.
        Directory.CreateDirectory(Path.Combine(dir, "objects"));
        Directory.CreateDirectory(Path.Combine(dir, "refs"));
        File.WriteAllText(Path.Combine(dir, "FETCH_HEAD"), string.Empty);

        Check("a repository now exists there", MirrorStore.HasRepository(url));
        Check("but an object store is not a download", !MirrorStore.Fetched(url));
        Check("and neither is a FETCH_HEAD from a fetch that failed",
            MirrorStore.NotReady(url) is { Length: > 0 });
        Check("the way out is named in the message",
            MirrorStore.NotReady(url)!.Contains("Fetch from origin", StringComparison.Ordinal));

        MirrorStore.MarkFetched(url);

        Check("only a fetch that returned success says so", MirrorStore.Fetched(url));
        Check("with a time that came from us, not from FETCH_HEAD",
            MirrorStore.FetchedAt(url) is { } marked && (DateTimeOffset.Now - marked).TotalMinutes < 5);
        Check("and now it can be read", MirrorStore.NotReady(url) is null);

        Check("two servers' copies stay apart",
            MirrorStore.PathFor(url) != MirrorStore.PathFor("https://github.com/x/api-signserver.example.net.git"));
        Check("and the root is absolute, never relative to wherever the app started",
            Path.IsPathRooted(MirrorStore.Root));
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* nothing to clean */ }
    }
}

// A repository that really is empty is a different sentence from a count of
// nothing, which reads as a measurement rather than as something being wrong.
Check("no branches is said in words",
    new ProbeReport(true, null, [], null, 0).Summary.Contains("no branches", StringComparison.Ordinal));
Check("and a real count is still a count",
    new ProbeReport(true, null, ["main", "dev"], "main", 0).Summary.Contains("2 branches", StringComparison.Ordinal));

// --- The guard that made downloading impossible -----------------------------
//
// A read must be refused until something has been fetched, or an empty copy
// reads as an empty repository. A fetch is the opposite — it is what makes the
// copy non-empty — so applying the same rule to it is a circle: the first
// download can never happen, and the window sits there telling you to press the
// button you just pressed. That shipped. It does not ship again.

{
    Check("a fetch reaches the server", GitCommand.ReachesServer(["fetch", "--all", "--prune"]));
    Check("so does ls-remote", GitCommand.ReachesServer(["ls-remote", "--heads", "https://h/x.git"]));
    Check("a ref listing does not", !GitCommand.ReachesServer(["for-each-ref", "--format=%(refname)"]));
    Check("nor does a merged check", !GitCommand.ReachesServer(["branch", "-a", "--merged", "main"]));
    Check("nor a log", !GitCommand.ReachesServer(["log", "--first-parent"]));
    Check("and nothing at all reaches nothing", !GitCommand.ReachesServer([]));

    var url = "https://git.example.com/root/api-signserver.example.net.git";
    var dir = MirrorStore.PathFor(url);

    try
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        Check("with no copy at all, a read is refused", MirrorStore.Refuse(url, filling: false) is not null);
        Check("and so is a fetch, having nowhere to put anything",
            MirrorStore.Refuse(url, filling: true) is not null);

        // What PrepareMirror leaves behind: a repository, and nothing in it.
        Directory.CreateDirectory(Path.Combine(dir, "objects"));

        Check("a read is still refused, because nothing has been fetched",
            MirrorStore.Refuse(url, filling: false) is not null);
        Check("but the fetch that would fix that is ALLOWED THROUGH",
            MirrorStore.Refuse(url, filling: true) is null);

        MirrorStore.MarkFetched(url);

        Check("once it has fetched, reads are allowed", MirrorStore.Refuse(url, filling: false) is null);
        Check("and so are further fetches", MirrorStore.Refuse(url, filling: true) is null);
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* nothing to clean */ }
    }
}

// --- Testing the connection, which is the question with four answers ---------
{
    var url = "https://git.example.com/root/api-signserver.example.net.git";

    var listed = RemoteTest.From(true,
        "a1b2c3d\trefs/heads/main\nf4e5d6c\trefs/heads/Golive-Redesign\n", string.Empty, url);

    Check("a server that answers is reachable", listed.Reachable);
    Check("and its branches are counted", listed.Branches == 2);
    Check("in a sentence that says what to do next",
        listed.Summary.Contains("2 branches") && listed.Summary.Contains("Download"));

    var empty = RemoteTest.From(true, string.Empty, string.Empty, url);
    Check("a repository with nothing in it is still reachable", empty.Reachable && empty.Branches == 0);
    Check("and says so rather than counting to zero",
        empty.Summary.Contains("no branches", StringComparison.Ordinal));

    // The four failures, which arrive looking identical and are not.
    var denied = RemoteTest.From(false, string.Empty,
        "fatal: could not read Username for 'https://git.example.com': terminal prompts disabled", url);
    Check("a refused sign-in is named as one", !denied.Reachable);
    Check("against the host it was refused by",
        denied.Summary.Contains("git.example.com", StringComparison.Ordinal));
    Check("and points at the boxes that fix it",
        denied.Summary.Contains("username and token", StringComparison.OrdinalIgnoreCase));

    var forbidden = RemoteTest.From(false, string.Empty,
        "remote: The project you were looking for could not be found.\nfatal: The requested URL returned error: 403", url);
    Check("signed in but not allowed is a different answer",
        forbidden.Summary.Contains("can't see that repository", StringComparison.Ordinal));
    Check("naming both things that could be wrong",
        forbidden.Summary.Contains("path", StringComparison.Ordinal)
        && forbidden.Summary.Contains("read access", StringComparison.Ordinal));

    var offline = RemoteTest.From(false, string.Empty,
        "fatal: unable to access 'https://git.example.com/x.git/': Could not resolve host: git.example.com", url);
    Check("a network that never got there says so",
        offline.Summary.Contains("Couldn't reach", StringComparison.Ordinal));
    Check("and mentions the thing people forget",
        offline.Summary.Contains("VPN", StringComparison.Ordinal));

    var untrusted = RemoteTest.From(false, string.Empty,
        "fatal: unable to access '...': SSL certificate problem: unable to get local issuer certificate", url);
    Check("a certificate git won't trust is its own answer",
        untrusted.Summary.Contains("certificate", StringComparison.Ordinal));

    Check("every failure keeps what git actually said",
        new[] { denied, forbidden, offline, untrusted }.All(t => t.Detail is { Length: > 0 }));
    Check("and none of them claims to be reachable",
        new[] { denied, forbidden, offline, untrusted }.All(t => !t.Reachable));

    // A URL with no scheme still has a host worth naming.
    Check("scp-style hosts are named too",
        RemoteTest.From(false, string.Empty, "Permission denied (publickey).",
            "git@git.example.com:root/app.git").Summary.Contains("git.example.com", StringComparison.Ordinal));
}

// --- What the details view shows -------------------------------------------
//
// Read by people and pasted into chat windows, so a credential must not survive
// a trip through it — even though nothing is supposed to put one in an argument.
{
    GitLog.Note("ls-remote --heads https://dev:glpat-SECRET@git.example.com/root/app.git",
        ok: false, "fatal: Authentication failed");

    var report = GitLog.Report();

    Check("a failed command is kept", report.Contains("ls-remote", StringComparison.Ordinal));
    Check("with what git said about it",
        report.Contains("Authentication failed", StringComparison.Ordinal));
    Check("and marked as a failure", report.Contains("FAIL", StringComparison.Ordinal));
    Check("but a secret that reached an argument does not survive the log",
        !report.Contains("SECRET", StringComparison.Ordinal));
    Check("while the host it was for is still readable",
        report.Contains("git.example.com", StringComparison.Ordinal));

    GitLog.Note("for-each-ref --format=x refs/heads", ok: true, string.Empty);
    Check("a command that worked is kept too, as context for the one that didn't",
        GitLog.Report().Contains("for-each-ref", StringComparison.Ordinal));
    Check("the newest is first", GitLog.Report().IndexOf("for-each-ref", StringComparison.Ordinal)
        < GitLog.Report().IndexOf("ls-remote", StringComparison.Ordinal));
}

// --- Text that isn't ASCII --------------------------------------------------
//
// .NET decodes a child process's output with the *console's* code page, which on
// a Vietnamese Windows install is CP1258 and elsewhere CP437 or CP1252. Git
// writes UTF-8. Read one as the other and a branch called
// "Fix-một-số-lỗi-phương-thức-ký" arrives as "Fix-má»™t-sá»‘-lá»—i…" — and
// stays wrong through the list, the filter, and the merge matching, because by
// then it is simply a different string.
{
    var psi = new System.Diagnostics.ProcessStartInfo("git")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
    }.ReadAsUtf8();

    Check("output is read as UTF-8", psi.StandardOutputEncoding?.CodePage == 65001);
    Check("so are errors", psi.StandardErrorEncoding?.CodePage == 65001);
    Check("and input is written as UTF-8", psi.StandardInputEncoding?.CodePage == 65001);

    // A BOM on stdin is three bytes of data git would read as part of the
    // credential request, where it belongs to no key.
    Check("with no byte-order mark on the way in",
        psi.StandardInputEncoding!.GetPreamble().Length == 0);

    // Nothing is set on a stream that isn't redirected: .NET throws on that.
    var plain = new System.Diagnostics.ProcessStartInfo("git").ReadAsUtf8();
    Check("and nothing is set on a stream nobody is reading",
        plain.StandardOutputEncoding is null && plain.StandardErrorEncoding is null);
}

// And nothing downstream of the decoding assumes ASCII either.
{
    string Row(params string[] fields) => string.Join(GitParse.Sep, fields);

    var vietnamese = "Fix-một-số-lỗi-phương-thức-ký";

    var refs = GitParse.Refs(string.Join('\n',
        Row($"refs/remotes/origin/{vietnamese}", "a1b2c3d", "1755600000", "Phan Thanh Đức", "sửa lỗi ký"),
        Row("refs/remotes/origin/main", "d4e5f6a", "1755000000", "dev", "merge")));

    var branch = refs.FirstOrDefault(b => b.Name == vietnamese);

    Check("a Vietnamese branch name survives the ref parser", branch is not null);
    Check("so does a Vietnamese author", branch!.Author == "Phan Thanh Đức");
    Check("and a Vietnamese commit subject", branch.Subject == "sửa lỗi ký");

    // The merged set and the ref list are matched by name, so a mangling on
    // either side would silently report shipped work as not shipped.
    var merged = GitParse.Merged($"refs/remotes/origin/{vietnamese}\n");
    Check("the merged set agrees with the branch list", merged.Contains(vietnamese));

    var standing = GitParse.Standing(refs,
        new Dictionary<string, string> { ["main"] = "production" },
        new Dictionary<string, IReadOnlySet<string>?> { ["main"] = merged },
        []);

    Check("so it is reported as having shipped",
        standing.Single(b => b.Name == vietnamese).Standings[0].State == Landed.Yes);

    // The merge timeline names branches in a commit subject, which is where a
    // mangled name would stop matching the branch it belongs to.
    Check("and a merge of it is attributed to it",
        GitParse.Merges("main", Row("aaa", "1755600000", "dev",
            $"Merge branch '{vietnamese}' into 'main'")).Single().Source == vietnamese);
}

// --- Which heading a branch belongs under -----------------------------------
//
// The list is grouped now, because forty branches sorted by date put something
// that shipped in March directly above something that has never shipped at all,
// and reading that is arithmetic. The grouping has one property worth more than
// any individual rule: the groups must *partition* the branches. A branch in two
// groups appears twice; a branch in none disappears from the window silently,
// which is the worse of the two.

{
    BranchLine Branch(string name, params TargetStanding[] standings) => new()
    {
        Name = name,
        Sha = "abc1234",
        Updated = DateTimeOffset.Now,
        Author = "dev",
        Subject = "work",
        Side = RefSide.Both,
        Standings = standings,
    };

    TargetStanding At(string env, Landed state) => new(env, env, state, null);

    var deploy = Branch("main", At("production", Landed.No)) with { IsTarget = true };
    var nowhere = Branch("feature/new", At("production", Landed.No), At("staging", Landed.No));
    var partly = Branch("feature/half", At("production", Landed.No), At("staging", Landed.Yes));
    var unsure = Branch("feature/lost", At("production", Landed.Unknown), At("staging", Landed.Yes));
    var shipped = Branch("feature/done", At("production", Landed.Yes), At("staging", Landed.Yes));
    var tidy = shipped with { Name = "feature/old", Side = RefSide.Local };

    Check("a deploy branch is the reference, not a thing measured",
        deploy.Group == BranchGroup.Deploy);
    Check("work that has reached nothing is open", nowhere.Group == BranchGroup.Open);
    Check("work in one environment of two is partly shipped", partly.Group == BranchGroup.Partly);
    Check("an unknown anywhere makes the whole row unknown", unsure.Group == BranchGroup.Unknown);
    Check("work in every environment has shipped", shipped.Group == BranchGroup.Shipped);
    Check("and a local-only copy of it is the tidy-up", tidy.Group == BranchGroup.Tidy);

    // An unknown is never quietly promoted to a yes, in the grouping any more
    // than in the pills — it is the failure this whole window is built to avoid.
    Check("an unknown is not shipped", unsure.Group != BranchGroup.Shipped);
    Check("nor is it partly shipped, even though something did land",
        unsure.Group != BranchGroup.Partly);

    // A deploy branch that also happens to satisfy another rule is still just a
    // deploy branch — otherwise main appears under both its own heading and
    // "shipped everywhere".
    var deployShipped = shipped with { Name = "main", IsTarget = true };
    Check("a deploy branch is only ever itself", deployShipped.Group == BranchGroup.Deploy);
    Check("and is never offered for deletion", !deployShipped.SafeToDelete);

    // The property that matters: every branch lands in exactly one group.
    var everything = new[] { deploy, nowhere, partly, unsure, shipped, tidy, deployShipped };
    var groups = Enum.GetValues<BranchGroup>();

    foreach (var branch in everything)
    {
        Check($"  {branch.Name} is in exactly one group",
            groups.Count(g => branch.Group == g) == 1);
    }

    Check("and between them the groups account for all of it",
        everything.Sum(b => groups.Count(g => b.Group == g)) == everything.Length);

    // A repository nobody has named deploy branches for has no standings at all.
    // Every branch reads as open, which is true only in the sense that the
    // question was never asked — so the window doesn't group at all there.
    var unasked = Branch("feature/x");
    Check("with nothing to measure against, nothing is claimed to be shipped",
        unasked.Group == BranchGroup.Open && !unasked.MergedSomewhere);
    Check("and the repository has nothing outstanding either",
        new ProjectSnapshot { Name = "x", Path = "/x" }.Outstanding == 0);
}


// --- Branch names this filesystem cannot store -----------------------------
//
// Git keeps one file per branch, so a branch name is also a path, and Windows
// refuses a path segment containing " * : < > ? | — which a branch named out of
// a ticket title very often has. Git calls the whole fetch a failure. The
// branches that did land are still current, so the only question that matters
// here is telling that apart from a fetch that really did fail.

const string nameFault =
    "From https://git.example.com/acme-sign/acme_signing_web\n"
  + " * [new branch]      Production -> origin/Production\n"
  + " ! [new branch]      Dashboard-\"Lượt-ký\"-/-Nhân-viên -> origin/x  (unable to update local ref)\n"
  + "error: cannot lock ref 'refs/remotes/origin/Dashboard-\"Lượt-ký\"-/-Nhân-viên': "
  + "unable to create directory for ./refs/remotes/origin/Dashboard-\"Lượt-ký\"-/-Nhân-viên\n";

Check("the branch git couldn't write down is named",
    GitRefs.Unstorable(nameFault) is ["refs/remotes/origin/Dashboard-\"Lượt-ký\"-/-Nhân-viên"]);
Check("...and the fetch counts as a partial success", GitRefs.OnlyUnstorable(nameFault));

// The identical sentence, a completely different problem. A read-only folder or
// a lock left behind by a killed git is something the user has to be told, so
// it must not be filed away as a footnote about branch names.
const string noWrite =
    "error: cannot lock ref 'refs/remotes/origin/good': "
  + "Unable to create '/m/refs/remotes/origin/good.lock': Permission denied\n";

Check("a permission failure is not a naming failure", !GitRefs.OnlyUnstorable(noWrite));
Check("...and is not blamed on a branch name", GitRefs.Unstorable(noWrite).Count == 0);
Check("a naming failure alongside a real one is a real failure",
    !GitRefs.OnlyUnstorable(nameFault + noWrite));

const string auth = "fatal: Authentication failed for 'https://git.example.com/x.git'\n";
Check("a refused sign-in is never downgraded", !GitRefs.OnlyUnstorable(auth));
Check("...not even next to a naming failure", !GitRefs.OnlyUnstorable(nameFault + auth));

Check("git's own summary line doesn't turn it back into a failure",
    GitRefs.OnlyUnstorable(nameFault
        + "error: some local refs could not be updated; try running\n"
        + " 'git remote prune origin' to remove any old, conflicting branches\n"));

Check("a leaf name Windows refuses is a naming failure too",
    GitRefs.OnlyUnstorable("error: cannot lock ref 'refs/remotes/origin/wo\"rd': "
        + "Unable to create 'C:/m/refs/remotes/origin/wo\"rd.lock': Invalid argument\n"));

// The terminator is "': " and not the next apostrophe, because a branch name is
// allowed to contain one and half a ref name helps nobody.
Check("a ref name containing a quote is extracted whole",
    GitRefs.Unstorable("error: cannot lock ref 'refs/remotes/origin/it's-fine': "
        + "unable to create directory for ./x\n") is ["refs/remotes/origin/it's-fine"]);

Check("a clean fetch is not a partial success", !GitRefs.OnlyUnstorable(""));
Check("neither is silence", !GitRefs.OnlyUnstorable(null));
Check("and there is nothing to explain", GitRefs.Explain([]) is null);

var told = GitRefs.Explain(GitRefs.Unstorable(nameFault));
Check("the explanation names the branch",
    told is not null && told.Contains("Lượt-ký", StringComparison.Ordinal));
Check("...says what actually causes it",
    told!.Contains("one file per branch", StringComparison.Ordinal));
Check("...and gives the fix that helps the whole team",
    told.Contains("Renaming", StringComparison.Ordinal));

Check("the remote prefix is dropped from a displayed ref",
    GitRefs.Short("refs/remotes/origin/feature/x").Contains("feature/x", StringComparison.Ordinal)
    && !GitRefs.Short("refs/remotes/origin/feature/x").Contains("refs/", StringComparison.Ordinal));
Check("a very long branch name is cut down to sentence size",
    GitRefs.Short("refs/heads/" + new string('b', 300)).Length < 60);

Check("a partial fetch is not a failure", FetchOutcome.Partial("x").Ok);
Check("a failed one is", !FetchOutcome.Failed("x").Ok);
Check("and a clean one carries no footnote", FetchOutcome.Done is { Ok: true, Warning: null });

var warnedProbe = new ProbeReport(true, null, ["main"], "main", 0, "One branch couldn't be stored.");
Check("a check's summary carries the footnote",
    warnedProbe.Summary.Contains("1 branch", StringComparison.Ordinal)
    && warnedProbe.Summary.EndsWith("One branch couldn't be stored.", StringComparison.Ordinal));


// Git takes negative refspecs, so the branch that cannot be written down can be
// left out and the rest fetched properly. That turns "nothing downloaded" into
// "everything except the one that was never storable".
var around = GitRefs.SkipArgs(GitRefs.Unstorable(nameFault));
Check("the retry skips the branch by name",
    around is not null && around.Any(a => a.StartsWith("^refs/heads/Dashboard-", StringComparison.Ordinal)));
Check("...keeps the positive refspec, which a negative one would otherwise replace",
    around!.Contains("+refs/heads/*:refs/remotes/origin/*"));
Check("...and prunes, like the fetch it stands in for", around.Contains("--prune"));

Check("the remote's real name is used, not a guess",
    GitRefs.SkipArgs(["refs/remotes/upstream/bad"]) is ["fetch", "--prune", "upstream",
        "+refs/heads/*:refs/remotes/upstream/*", "^refs/heads/bad"]);
Check("a branch with slashes in it keeps them",
    GitRefs.SkipArgs(["refs/remotes/origin/a/b/c"])!.Contains("^refs/heads/a/b/c"));
Check("two remotes at once is not something to guess at",
    GitRefs.SkipArgs(["refs/remotes/origin/a", "refs/remotes/other/b"]) is null);
Check("a ref that isn't a remote-tracking one is left alone",
    GitRefs.SkipArgs(["refs/tags/v1"]) is null);
Check("nothing to skip means no retry", GitRefs.SkipArgs([]) is null);

Check("the explanation says the branch was skipped, not that it failed",
    GitRefs.Explain(GitRefs.Unstorable(nameFault))!.Contains("skipped", StringComparison.Ordinal));

// --- Filing repositories under headings ------------------------------------

Check("a blank group is no group", ProjectGroups.Clean("   ") is null);
Check("a group keeps the spelling it was given", ProjectGroups.Clean("  Mắt Bão  ") == "Mắt Bão");
Check("a heading can't smuggle in a newline", ProjectGroups.Clean("Mắt\tBão\n\nCloud") == "Mắt Bão Cloud");
Check("a very long heading is cut to fit the rail",
    ProjectGroups.Clean(new string('x', 300))!.Length == ProjectGroups.MaxLength);

Check("case alone doesn't make a second group", ProjectGroups.Same("Backend", "backend"));
Check("different words do", !ProjectGroups.Same("Backend", "Frontend"));
Check("no group and no group are the same band", ProjectGroups.Same(null, "  "));

Check("the first spelling of a group is the one kept",
    ProjectGroups.Names(["Backend", "backend", "BACKEND"]) is ["Backend"]);
Check("...in the order the groups first appear",
    ProjectGroups.Names(["Web", "Backend", "web"]) is ["Web", "Backend"]);
Check("a later spelling is folded onto the first",
    ProjectGroups.Canonical("BACKEND", ["Backend", "Web"]) == "Backend");
Check("a brand new group is left as typed", ProjectGroups.Canonical("New", ["Backend"]) == "New");

(string Name, string? Group)[] filing =
[
    ("a", "Backend"), ("b", null), ("c", "backend"), ("d", "Web"), ("e", "   "),
];

var bands = ProjectGroups.Arrange(filing, x => x.Group);

Check("bands come in the order their first project does",
    bands.Select(b => b.Title).SequenceEqual(["Backend", "Web", ProjectGroups.LooseTitle]));
Check("a differently-cased group joins the first one",
    bands[0].Items.Select(i => i.Name).SequenceEqual(["a", "c"]));
Check("the leftovers come last, and are not a named group", !bands[^1].Named);

// The property worth more than any single case above: a project in two bands is
// a project drawn twice, and one in no band disappears out of the rail without
// saying so — which is the worse of the two, and silent.
Check("every project lands in exactly one band",
    bands.SelectMany(b => b.Items).Select(i => i.Name)
         .OrderBy(n => n, StringComparer.Ordinal)
         .SequenceEqual(["a", "b", "c", "d", "e"]));

Check("nothing grouped is one unnamed band, so the rail looks untouched",
    ProjectGroups.Arrange(filing.Select(x => (x.Name, Group: (string?)null)).ToList(), x => x.Group)
        is [{ Named: false, Title: "" }]);
Check("no projects is no bands at all", ProjectGroups.Arrange<string>([], _ => null).Count == 0);

Check("a fold for a group nobody uses is dropped",
    ProjectGroups.Live(["backend", "gone"], ["Backend"]) is ["backend"]);
Check("folds are remembered by key, not by spelling",
    ProjectGroups.Live(["BACKEND"], ["backend"]) is ["backend"]);

Reset();
WriteConfig(new
{
    projects = new object[]
    {
        new { name = "one", path = "/a", where = "workspace", group = "  Backend " },
        new { name = "two", path = "/b", where = "workspace", group = "BACKEND" },
        new { name = "three", path = "/c", where = "workspace" },
    },
    collapsedGroups = new[] { "backend", "nosuch" },
});

var filed = CoderConfig.Load();
Check("a group survives the file", filed.Projects[0].Group == "Backend");
Check("...and a second spelling is filed under the first in the file too",
    filed.Projects[1].Group == "Backend");
Check("no group stays no group", filed.Projects[2].Group is null);
Check("a fold for a group that exists is kept", filed.CollapsedGroups.Contains("backend"));
Check("a fold for a group that doesn't is dropped", !filed.CollapsedGroups.Contains("nosuch"));

foreach (var entry in filed.Projects) entry.Group = null;
filed.SetProjects(filed.Projects);
Check("emptying the last group in a band drops its fold too", filed.CollapsedGroups.Length == 0);

var grouped = new ProjectDraft { Host = RepoHost.Workspace, Path = "/home/coder/x", Name = "x", Group = "  Web  " };
Check("a draft's group reaches the entry cleaned", grouped.ToEntry().Group == "Web");
Check("...and comes back when it is edited again",
    ProjectDraft.From(grouped.ToEntry()).Group == "Web");

// --- Against real git, not a fake ------------------------------------------
//
// Everything above tests the parsers on strings I wrote. This builds an actual
// repository, fetches it into an actual bare copy shaped the way the app's
// mirror is, and runs the actual reader over it. It is the only check here that
// would notice git changing what it prints, or the mirror being set up in a way
// that makes a server's branch look like a local one you could delete.
//
// Skipped, loudly, where git isn't installed.

if (GitCli.Resolve(new CoderConfig()) is not { } gitExe)
{
    Console.WriteLine("SKIP  live git checks — no git on this machine");
}
else
{
    var root = Path.Combine(Path.GetTempPath(), "mascot-mirror-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    async Task<GitOutput> Git(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(gitExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = dir,
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        foreach (var (key, value) in new[]
                 {
                     ("GIT_AUTHOR_NAME", "dev"), ("GIT_AUTHOR_EMAIL", "dev@example.com"),
                     ("GIT_COMMITTER_NAME", "dev"), ("GIT_COMMITTER_EMAIL", "dev@example.com"),
                     ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_TERMINAL_PROMPT", "0"),
                 })
            psi.EnvironmentVariables[key] = value;

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode == 0 ? GitOutput.Good(stdout) : GitOutput.Bad(stderr);
    }

    try
    {
        // A repository shaped like a real one: a deploy branch, work that landed
        // on it, work that hasn't, and a second deploy branch off to one side.
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);

        await Git(src, "init", "-q", "-b", "main");
        await Git(src, "commit", "-q", "--allow-empty", "-m", "root");
        await Git(src, "checkout", "-q", "-b", "feature/landed");
        await Git(src, "commit", "-q", "--allow-empty", "-m", "landed work");
        await Git(src, "checkout", "-q", "main");
        await Git(src, "merge", "-q", "--no-ff", "feature/landed",
                  "-m", "Merge branch 'feature/landed' into 'main'");
        await Git(src, "checkout", "-q", "-b", "feature/open");
        await Git(src, "commit", "-q", "--allow-empty", "-m", "open work");
        await Git(src, "checkout", "-q", "main");

        // The copy, set up exactly the way PrepareMirror sets one up.
        var mirror = Path.Combine(root, "mirror");
        Check("a bare copy is created", (await Git(root, "init", "--bare", mirror)).Ok);
        Check("pointed at the repository", (await Git(mirror, "remote", "add", "origin", src)).Ok);
        Check("and fetched", (await Git(mirror, "fetch", "--all", "--prune")).Ok);

        var reader = new GitReader((project, args, _) => Git(project.Path, [.. args]));

        var project = new GitProject
        {
            Name = "fixture",
            Path = mirror,
            Host = RepoHost.Remote,
            Deployed = new Dictionary<string, string> { ["main"] = "production" },
        };

        var snap = await reader.ReadAsync(project, CancellationToken.None);

        Check("real git: the bare copy reads", snap.Ok);
        Check("real git: three branches", snap.Branches.Count == 3);
        Check("real git: they are the server's, not local ones",
            snap.Branches.All(b => b.Side == RefSide.Remote));
        Check("real git: so none is offered for deletion",
            snap.Branches.All(b => !b.SafeToDelete));
        Check("real git: no current branch is claimed", snap.CurrentBranch is null);

        var landed = snap.Branches.FirstOrDefault(b => b.Name == "feature/landed");
        var open = snap.Branches.FirstOrDefault(b => b.Name == "feature/open");

        Check("real git: the branch that shipped says so",
            landed is { } l && l.Standings.Single().State == Landed.Yes);
        Check("real git: with the date it landed",
            landed?.Standings.Single().LandedAt is not null);
        Check("real git: the branch that hasn't says that",
            open is { } o && o.Standings.Single().State == Landed.No);
        Check("real git: main is recognised as the deploy branch",
            snap.Branches.Single(b => b.Name == "main").IsTarget);
        Check("real git: the merge is in the timeline",
            snap.Merges.Any(m => m is { Target: "main", Source: "feature/landed" }));

        // Counts are optional — git before 2.41 can't produce them — so this
        // asserts only that what comes back is consistent, never that it exists.
        Check("real git: ahead/behind is either absent or right",
            open?.Ahead is null || open.Ahead > 0);

        var probe = await reader.ProbeAsync(project, CancellationToken.None);
        Check("real git: the editor's check agrees", probe.Ok && probe.Branches.Count == 3);
        Check("real git: and offers the branch names to pick from",
            probe.Branches.Contains("feature/open") && probe.Branches.Contains("main"));

        // The same reader against an ordinary checkout, which does have a work
        // tree — the path the bare handling must not have broken.
        var working = new GitProject
        {
            Name = "src",
            Path = src,
            Host = RepoHost.Local,
            Deployed = new Dictionary<string, string> { ["main"] = "production" },
        };

        File.WriteAllText(Path.Combine(src, "scratch.txt"), "uncommitted");

        var live = await reader.ReadAsync(working, CancellationToken.None);
        Check("real git: an ordinary checkout still reads", live.Ok);
        Check("real git: and does know what branch it is on", live.CurrentBranch == "main");
        Check("real git: and what is uncommitted in it", live.DirtyFiles == 1);

        // The download, start to finish, in the order the app does it — which is
        // the sequence the circular guard broke. Everything below ran green as
        // unit tests while the app could not download a single repository,
        // because the step that failed was the wiring between them.
        {
            var url = "https://git.example.invalid/team/fixture.git";
            var mirrorDir = MirrorStore.PathFor(url);

            try
            {
                if (Directory.Exists(mirrorDir)) Directory.Delete(mirrorDir, recursive: true);

                Check("live: nothing to read before anything is downloaded",
                    MirrorStore.Refuse(url, filling: false) is not null);

                // 1. PrepareMirror: somewhere to fetch into, pointed at the server.
                Directory.CreateDirectory(MirrorStore.Root);
                Check("live: the bare copy is created",
                    (await Git(MirrorStore.Root, "init", "--bare", mirrorDir)).Ok);
                Check("live: and pointed at the repository",
                    (await Git(mirrorDir, "remote", "add", "origin", src)).Ok);

                // 2. The guard must let the fetch through. This is the whole bug:
                //    the copy is empty, and the fetch is what stops it being empty.
                Check("live: a read is still refused", MirrorStore.Refuse(url, filling: false) is not null);
                Check("live: the fetch is not", MirrorStore.Refuse(url, filling: true) is null);

                // 3. The fetch itself.
                var pulled = await Git(mirrorDir, "fetch", "--all", "--prune");
                Check("live: the fetch runs and succeeds", pulled.Ok);

                // 4. Only now is it downloaded, and only because that returned.
                Check("live: nothing claims it fetched before we say so", !MirrorStore.Fetched(url));
                MirrorStore.MarkFetched(url);
                Check("live: and now it is readable", MirrorStore.Refuse(url, filling: false) is null);

                // 5. Which is the point of all of it.
                var downloaded = await reader.ReadAsync(
                    new GitProject
                    {
                        Name = "fixture",
                        Path = mirrorDir,
                        Host = RepoHost.Remote,
                        Deployed = new Dictionary<string, string> { ["main"] = "production" },
                    },
                    CancellationToken.None);

                Check("live: the downloaded copy reads", downloaded.Ok);
                Check("live: with the branches that were on the server",
                    downloaded.Branches.Count == 3);
                Check("live: and the merge state that is the point of the window",
                    downloaded.Branches.Single(b => b.Name == "feature/landed").Standings[0].State
                        == Landed.Yes);
            }
            finally
            {
                try { Directory.Delete(mirrorDir, recursive: true); } catch { /* nothing to clean */ }
            }
        }

        // A branch named the way this user's branches are actually named, read
        // back through a real process. This is the half a unit test can't do:
        // it exercises the decoding, not just the parsing.
        await Git(src, "branch", "Fix-một-số-lỗi-phương-thức-ký");

        var accented = await reader.ReadAsync(working, CancellationToken.None);
        Check("real git: a Vietnamese branch name comes back as itself",
            accented.Branches.Any(b => b.Name == "Fix-một-số-lỗi-phương-thức-ký"));
        Check("real git: and not as mojibake",
            !accented.Branches.Any(b => b.Name.Contains("Ã", StringComparison.Ordinal)
                                        || b.Name.Contains("á»", StringComparison.Ordinal)));

        // A branch this machine cannot write down, against real git.
        //
        // On Windows it is the branch name itself: " is illegal in a path, and
        // git stores one file per branch. That can't be arranged on Linux, where
        // the only illegal characters are / and NUL — so the same failure is
        // produced by taking write permission off the refs directory, which
        // makes git print the identical sentence through the identical code
        // path. What is being checked is the classification and the salvage, and
        // both are the same either way.
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP  unstorable-ref checks — arranged with unix permissions");
        }
        else
        {
            var awkward = Path.Combine(root, "awkward");

            Check("real git: a second bare copy is created",
                (await Git(root, "init", "--bare", awkward)).Ok);
            Check("real git: pointed at the repository",
                (await Git(awkward, "remote", "add", "origin", src)).Ok);
            Check("real git: and fetched once cleanly",
                (await Git(awkward, "fetch", "--all", "--prune")).Ok);

            var already = await reader.ReadAsync(
                new GitProject { Name = "awkward", Path = awkward, Host = RepoHost.Remote },
                CancellationToken.None);
            var before = already.Branches.Count;

            // Named the way the branch in the report was named: a ticket title
            // with quotes and slashes in it.
            await Git(src, "branch", "Dashboard-\"Lượt-ký\"---Tổng-quan/-Nhân-viên");

            var refsDir = Path.Combine(awkward, "refs", "remotes", "origin");
            var was = File.GetUnixFileMode(refsDir);
            File.SetUnixFileMode(refsDir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var blocked = await Git(awkward, "fetch", "--all", "--prune");
            File.SetUnixFileMode(refsDir, was);

            Check("real git: a ref it can't store fails the fetch", !blocked.Ok);
            Check("real git: and the failure is recognised as a naming one",
                GitRefs.OnlyUnstorable(blocked.Error));
            Check("real git: the branch that couldn't be stored is named",
                GitRefs.Unstorable(blocked.Error)
                    .Any(r => r.Contains("Lượt-ký", StringComparison.Ordinal)));
            Check("real git: which is worth an explanation",
                GitRefs.Explain(GitRefs.Unstorable(blocked.Error)) is not null);

            // The whole point of treating it as partial: everything else is
            // still here, and still readable.
            var salvaged = await reader.ReadAsync(
                new GitProject { Name = "awkward", Path = awkward, Host = RepoHost.Remote },
                CancellationToken.None);

            Check("real git: the repository still reads after that fetch", salvaged.Ok);
            Check("real git: and every other branch is still there",
                salvaged.Branches.Count >= before && before > 0);


            // The whole recovery, end to end: the fetch that can't write a ref,
            // then the retry that steps around it, then a reading that has
            // everything else in it.
            var skip = GitRefs.SkipArgs(GitRefs.Unstorable(blocked.Error));
            Check("real git: a way around it is worked out", skip is not null);

            // A branch that appeared while the copy was stuck, so the retry has
            // something real to bring down. Without one, a retry that fetched
            // nothing at all would pass just as happily — and that is the exact
            // failure mode here, since a negative refspec on the command line
            // replaces the remote's configured one rather than adding to it.
            await Git(src, "branch", "arrived-later");

            var worked = await Git(awkward, [.. skip!]);
            Check("real git: and the retry fetches cleanly", worked.Ok);

            var after = await reader.ReadAsync(
                new GitProject { Name = "awkward", Path = awkward, Host = RepoHost.Remote },
                CancellationToken.None);

            Check("real git: the retry really does fetch, positive refspec and all",
                after.Ok && after.Branches.Any(b => b.Name == "arrived-later"));
            Check("real git: so the copy has one more branch than before",
                after.Branches.Count == before + 1);
            Check("real git: and the one that could never be stored is still absent",
                !after.Branches.Any(b => b.Name.Contains("Lượt-ký", StringComparison.Ordinal)));

            // The other direction. A second new branch means an ordinary
            // permission failure lands in the same output, and a permission
            // failure is not a footnote — the branches on disk may genuinely
            // not be current, so this one has to stay a failure.
            await Git(src, "branch", "also-new");
            File.SetUnixFileMode(refsDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var mixed = await Git(awkward, "fetch", "--all", "--prune");
            File.SetUnixFileMode(refsDir, was);

            Check("real git: a naming failure mixed with a permission one stays a failure",
                !mixed.Ok && !GitRefs.OnlyUnstorable(mixed.Error));
        }


    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { /* a test's litter */ }
    }
}

Reset();
Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILED");
return failures;
