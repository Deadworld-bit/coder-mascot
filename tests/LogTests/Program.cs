using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoderMascot.Core;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

int failures = 0, checks = 0;
void Check(string desc, bool ok)
{
    checks++;
    if (!ok) { failures++; Console.WriteLine($"FAIL: {desc}"); }
}

// CoderConfig and CrashLog write under the config dir; keep them off the real one.
var scratch = Directory.CreateTempSubdirectory("logtests-").FullName;
var xdg = Path.Combine(scratch, "config");
Directory.CreateDirectory(xdg);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);

var fallback = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
var vn = TimeSpan.FromHours(7);

List<LogEntry> Parse(string text, DateOnly? day = null, bool middle = false) =>
    LogParse.Entries(LogFile.Lines(new StringReader(text), LogFile.MaxLineChars), "f.txt", day, fallback, middle).ToList();

// --- text formats ---
{
    var e = Parse("2026-10-23 10:15:30.123 +07:00 [INF] User 5 signed in\r\n" +
                  "2026-10-23 10:15:31.000 +07:00 [ERR] Signing failed\r\n" +
                  "System.InvalidOperationException: boom\r\n   at A.B()\r\n");
    Check("serilog: two entries", e.Count == 2);
    Check("serilog: time keeps its zone", e[0].Time == new DateTimeOffset(2026, 10, 23, 10, 15, 30, 123, vn));
    Check("serilog: levels", e[0].Level == LogSeverity.Info && e[1].Level == LogSeverity.Error);
    Check("serilog: message loses time and level", e[0].Message == "User 5 signed in");
    Check("serilog: stack trace stays with its error", e[1].Detail == "System.InvalidOperationException: boom\n   at A.B()");
    Check("serilog: text as written",
        e[1].Text == "2026-10-23 10:15:31.000 +07:00 [ERR] Signing failed\nSystem.InvalidOperationException: boom\n   at A.B()");
    Check("serilog: line numbers", e[0].Line == 1 && e[1].Line == 2);
    Check("serilog: no detail is null", e[0].Detail is null && e[0].Text == e[0].FirstLine);
}
{
    var e = Parse("2026-10-23 08:00:00.500 [Information] IdMbi.Controllers.Auth: Login ok\n" +
                  "2026-10-23 08:00:01.000 [Error] IdMbi.Services.Tvan: Send failed\n" +
                  "System.Net.Http.HttpRequestException: timeout\n");
    Check("id-mbi: levels", e.Count == 2 && e[0].Level == LogSeverity.Info && e[1].Level == LogSeverity.Error);
    Check("id-mbi: category stays in the message", e[0].Message == "IdMbi.Controllers.Auth: Login ok");
    Check("id-mbi: exception is detail", e[1].Detail == "System.Net.Http.HttpRequestException: timeout");
    Check("id-mbi: unzoned time is the clock as written", e[0].Time?.DateTime == new DateTime(2026, 10, 23, 8, 0, 0, 500));
}
{
    var e = Parse("2026-10-23 10:15:30.1234|INFO|App.Home|Hello\n");
    Check("nlog", e.Count == 1 && e[0].Level == LogSeverity.Info && e[0].Message == "App.Home|Hello");
    Check("nlog: four-digit fraction", e[0].Time?.DateTime == new DateTime(2026, 10, 23, 10, 15, 30).AddTicks(1234000));

    e = Parse("2026-10-23 10:15:30,123 [12] ERROR App.Svc - Broke\n");
    Check("log4net", e.Count == 1 && e[0].Level == LogSeverity.Error && e[0].Message == "[12] App.Svc - Broke");
    Check("log4net: comma fraction", e[0].Time?.Millisecond == 123);
}
{
    var day = new DateOnly(2026, 10, 23);
    var e = Parse("[10:15:30 INF] Started\n[10:15:31 ERR] Oops\n  at X()\n", day);
    Check("time only: dated from the file name", e.Count == 2 && e[0].Time?.DateTime == new DateTime(2026, 10, 23, 10, 15, 30));
    Check("time only: levels and messages", e[0].Message == "Started" && e[1].Level == LogSeverity.Error && e[1].Message == "Oops");
    Check("time only: continuation", e[1].Detail == "  at X()");

    e = Parse("[10:15:30 WRN] Hm\n");
    Check("time only, no date: no time, line kept", e.Count == 1 && e[0].Time is null && e[0].Level == LogSeverity.Warning
        && e[0].Message == "[10:15:30 WRN] Hm");

    e = Parse("10:15:30 started\n");
    Check("a bare time of day without a level is not a header", e.Count == 1 && e[0].Time is null && e[0].Level == LogSeverity.Unknown);

    e = Parse("2026-10-23 10:00:00 [INF] a\n10:15:30 user pressed the INFO button\n", day);
    Check("a time of day with a level word further on is continuation", e.Count == 1 && e[0].Detail?.StartsWith("10:15:30") == true);
}
{
    var e = Parse("info: Microsoft.Hosting.Lifetime[14]\n      Now listening on: http://[::]:8080\n" +
                  "fail: Microsoft.AspNetCore.Server.Kestrel[13]\n      Connection id \"0HN\" error\n      System.Exception: x\n");
    Check("aspnet console: two entries", e.Count == 2);
    Check("aspnet console: the indented line is the message",
        e[0].Level == LogSeverity.Info && e[0].Message == "Now listening on: http://[::]:8080");
    Check("aspnet console: fail is an error", e[1].Level == LogSeverity.Error && e[1].Message == "Connection id \"0HN\" error");
    Check("aspnet console: exception kept", e[1].Detail?.Contains("System.Exception: x") == true);

    // docker logs -t stamps every line, continuation lines included, trimming the fraction's trailing zeros.
    e = Parse("2026-10-23T03:15:30.123456789Z fail: App.Worker[0]\n" +
              "2026-10-23T03:15:30.12345679Z       Job crashed\n" +
              "2026-10-23T03:15:30.1234568Z       System.Exception: x\n" +
              "2026-10-23T03:15:31Z info: App.Worker[0]\n" +
              "2026-10-23T03:15:31.5Z       Done\n");
    Check("docker -t: one entry per message", e.Count == 2);
    Check("docker -t: nine-digit fraction, UTC",
        e[0].Time == new DateTimeOffset(2026, 10, 23, 3, 15, 30, TimeSpan.Zero).AddTicks(1234567));
    Check("docker -t: aspnet message promoted", e[0].Level == LogSeverity.Error && e[0].Message == "Job crashed");
    Check("docker -t: exception stays with its error", e[0].Detail?.Contains("System.Exception: x") == true
        && !e[0].Detail!.Contains("2026-10-23T"));
    Check("docker -t: next entry", e[1].Level == LogSeverity.Info && e[1].Message == "Done");

    e = Parse("2026-10-23T03:15:30.1Z [10:15:30 ERR] Oops\n2026-10-23T03:15:30.2Z System.Exception: boom\n2026-10-23T03:15:30.3Z    at X()\n");
    Check("docker -t around serilog console", e.Count == 1 && e[0].Level == LogSeverity.Error
        && e[0].Time == new DateTimeOffset(2026, 10, 23, 3, 15, 30, 100, TimeSpan.Zero)
        && e[0].Detail == "System.Exception: boom\n   at X()");

    e = Parse("2026-10-23T03:15:30.123Z [INF] plain iso\n2026-10-23T03:15:31.000Z [ERR] next\n");
    Check("an ISO log is not mistaken for docker", e.Count == 2 && e[0].Message == "plain iso" && e[1].Level == LogSeverity.Error);
}
{
    var e = Parse("2026-10-23 10:15:30 Call API error: timeout\n2026-10-23 10:15:31 Sending debug info to server\n" +
                  "2026-10-23 10:15:32 Retry after error, attempt 2\n2026-10-23 10:15:33 Error saving invoice\n");
    Check("a level word mid-sentence is not the level", e.Take(3).All(x => x.Level == LogSeverity.Unknown));
    Check("and stays in the message", e[0].Message == "Call API error: timeout" && e[1].Message == "Sending debug info to server");
    Check("a leading level word still counts", e[3].Level == LogSeverity.Error && e[3].Message == "saving invoice");
}
{
    var e = Parse("first line\n\nERROR: second\n[WARN] third\nStarting the error handler\n");
    Check("plain text: one entry per line, blanks skipped", e.Count == 4);
    Check("plain text: level from a leading word",
        e[0].Level == LogSeverity.Unknown && e[1].Level == LogSeverity.Error && e[2].Level == LogSeverity.Warning);
    Check("plain text: a level word later in the line doesn't count", e[3].Level == LogSeverity.Unknown);
    Check("plain text: whole line is the message", e[1].Message == "ERROR: second");
    Check("plain text: untimed entries sort at the fallback", e.All(x => x.SortTime == fallback && x.Time is null));
}
{
    var e = Parse("05/10/2026 08:00:00 INFO a\n13/10/2026 08:00:00 INFO b\n10/13/2026 08:00:00 INFO c\n10/13/2026 8:00:00 PM INFO d\n");
    Check("dates: day first by default", e[0].Time?.Month == 10 && e[0].Time?.Day == 5);
    Check("dates: 13 can only be a day", e[1].Time?.Month == 10 && e[1].Time?.Day == 13);
    Check("dates: US order when the second number can't be a month", e[2].Time?.Month == 10 && e[2].Time?.Day == 13);
    Check("dates: PM", e[3].Time?.Hour == 20 && e[3].Message == "d");

    e = Parse("2026-10-23 10:00:00 [INF] a\n2026-13-45 10:00:00 x\n");
    Check("an impossible date is a continuation", e.Count == 1 && e[0].Detail == "2026-13-45 10:00:00 x");

    e = Parse("2026-10-23 10:15:30 Zalo sent\n");
    Check("a word starting with Z is not a zone", e.Count == 1 && e[0].Message == "Zalo sent");

    e = Parse("２０２６-10-23 10:00:00 [INF] fullwidth\n");
    Check("non-ASCII digits are not a timestamp", e.Count == 1 && e[0].Time is null);
}
{
    var e = Parse("   at X()\n   at Y()\n2026-10-23 10:00:00 [INF] a\n", middle: true);
    Check("from the middle: leading continuation dropped", e.Count == 1 && e[0].Message == "a");

    e = Parse("banner\n2026-10-23 10:00:00 [INF] a\n");
    Check("from the start: leading lines kept", e.Count == 2 && e[0].Message == "banner");
    Check("leading untimed lines sort with the first timestamp", e[0].SortTime == e[1].Time);

    e = Parse("2026-10-23 10:00:00 [INF] Body:\n{\"a\":1}\n");
    Check("a JSON body under a line is continuation", e.Count == 1 && e[0].Detail == "{\"a\":1}");

    var lines = new StringBuilder("2026-10-23 10:00:00 [ERR] big\n");
    for (var i = 0; i < 1000; i++) lines.Append("   at Frame").Append(i).Append("()\n");
    e = Parse(lines.ToString());
    Check("continuation capped by lines", e.Count == 1 && e[0].Cut
        && e[0].Detail!.Split('\n').Length == LogParse.MaxDetailLines + 1
        && e[0].Detail!.EndsWith("… 500 more lines not shown"));

    e = Parse("2026-10-23 10:00:00 [ERR] wide\n" + new string('x', 40_000) + "\n");
    Check("continuation capped by size", e.Count == 1 && e[0].Cut && e[0].Detail!.Length < LogParse.MaxDetailChars + 40);
}

// --- JSON lines ---
{
    var clef = """{"@t":"2026-10-23T03:15:30.1234567Z","@mt":"Signed {DocumentId} for {@User} in {Elapsed:0.00} ms","DocumentId":42,"User":{"Name":"a"},"Elapsed":12.3456,"@r":["12.35"],"SourceContext":"MB.Sign.Controller"}""";
    var clefErr = """{"@t":"2026-10-23T03:15:31Z","@mt":"Failed {Message}","@l":"Error","@x":"System.Exception: boom\n   at X.Y()","Message":"user text","Level":"Debug"}""";
    var e = Parse(clef + "\n" + clefErr + "\n");
    Check("clef: two entries", e.Count == 2);
    Check("clef: no @l is Information", e[0].Level == LogSeverity.Info);
    Check("clef: template rendered, @r for formatted tokens",
        e[0].Message == "Signed 42 for {\"Name\":\"a\"} in 12.35 ms");
    Check("clef: UTC time", e[0].Time == new DateTimeOffset(2026, 10, 23, 3, 15, 30, TimeSpan.Zero).AddTicks(1234567));
    Check("clef: structured, text is the line", e[0].Structured && e[0].Text == clef);
    Check("clef: properties shown, @r hidden",
        e[0].Detail?.Contains("SourceContext: MB.Sign.Controller") == true && !e[0].Detail!.Contains("@r"));
    Check("clef: @l wins over a property called Level", e[1].Level == LogSeverity.Error);
    Check("clef: a property called Message is data", e[1].Message == "Failed user text");
    Check("clef: exception leads the detail", e[1].Detail?.StartsWith("System.Exception: boom\n   at X.Y()") == true);
    Check("clef: the Level property is listed", e[1].Detail?.Contains("Level: Debug") == true);

    e = Parse("""{"@t":"2026-10-23T03:15:30Z","@mt":"{{literal}} {Missing} {Count}","Count":3}""" + "\n");
    Check("clef: escaped braces and missing values", e[0].Message == "{literal} {Missing} 3");

    e = Parse("""{"@t":"2026-10-23T03:15:30Z","@m":"line1\nline2"}""" + "\n");
    Check("clef: a multi-line message", e[0].Message == "line1" && e[0].Detail?.StartsWith("line2") == true);

    e = Parse("""{"Timestamp":"2026-10-23T10:15:30.1000000+07:00","Level":"Warning","MessageTemplate":"Slow {Ms}","RenderedMessage":"Slow 900","Properties":{"Ms":900,"RequestPath":"/api/x"}}""" + "\n");
    Check("serilog json: level and rendered message", e[0].Level == LogSeverity.Warning && e[0].Message == "Slow 900");
    Check("serilog json: nested properties listed", e[0].Detail?.Contains("RequestPath: /api/x") == true
        && !e[0].Detail!.Contains("Properties:"));

    e = Parse("""{"Timestamp":"2026-10-23T10:15:30+07:00","Level":"Error","MessageTemplate":"Slow {Ms}","Properties":{"Ms":900}}""" + "\n");
    Check("serilog json: template rendered from nested properties", e[0].Message == "Slow 900");

    e = Parse("""{"time":1761189330000,"level":50,"msg":"pino err"}""" + "\n");
    Check("pino: epoch ms and numeric level", e[0].Level == LogSeverity.Error && e[0].Time?.Year == 2025 && e[0].Message == "pino err");

    e = Parse("{\"a\":1}\n[1,2]\n");
    Check("JSON with no time is just text", e.Count == 2 && e.All(x => !x.Structured));

    var deep = "{\"@t\":\"2026-10-23T00:00:00Z\",\"x\":" + new string('[', 100) + new string(']', 100) + "}";
    e = Parse(deep + "\n");
    Check("deep JSON is text, not a crash", e.Count == 1 && !e[0].Structured);

    e = Parse("2026-10-23 10:00:00 [INF] Response:\n{\"timestamp\":\"2026-10-23T10:00:00Z\",\"status\":500,\"error\":\"x\"}\n");
    Check("a timed JSON body under a text entry is still continuation", e.Count == 1 && e[0].Detail?.Contains("\"status\":500") == true);

    e = Parse("2026-10-23 10:00:00 [INF] text\n" + """{"@t":"2026-10-23T10:00:01Z","@mt":"clef"}""" + "\n");
    Check("a CLEF line under a text entry is an entry", e.Count == 2 && e[1].Structured);

    e = Parse("""{"@t":"2026-10-23T10:00:01Z","@mt":"clef"}""" + "\n   at X()\n");
    Check("a JSON entry takes no continuation", e.Count == 2 && e[0].Detail is null && e[1].Message == "at X()");
}
{
    var backslash = ((char)92).ToString();
    var hostile = new[]
    {
        "2026-10-23 10:15:30 +14:30 [INF] beyond the last zone",
        "{\"a\":\"" + (char)0xD83D + "\"}",
        "{\"@t\":\"2026-10-23T00:00:00Z\",\"@mt\":\"" + backslash + "uD800\"}",
        "{\"@t\":\"2026-10-23T00:00:00Z\",\"" + backslash + "uDC00\":1}",
    };

    foreach (var line in hostile)
    {
        List<LogEntry>? e = null;
        try { e = Parse(line + "\n"); } catch (Exception ex) { Console.WriteLine($"  threw {ex.GetType().Name}"); }
        Check($"no throw: {line[..Math.Min(30, line.Length)]}", e is { Count: 1 });
    }

    var emoji = char.ConvertFromUtf32(0x1F600);
    var cutAt = "{\"a\":\"" + new string('x', LogFile.MaxLineChars - 7) + emoji + "\"}";
    var cut = Parse(cutAt + "\n");
    Check("a line cut mid-emoji: no half character, marked, no throw", cut.Count == 1 && !cut[0].Structured
        && cut[0].FirstLine.EndsWith("…") && !char.IsHighSurrogate(cut[0].FirstLine[^2]));

    var tokens = string.Concat(Enumerable.Repeat("{P}", 20_000));
    var bomb = "{\"@t\":\"2026-10-23T10:00:00Z\",\"@mt\":\"x" + backslash + "n" + tokens + "\",\"P\":\"" + new string('a', 1000) + "\"}";
    var watch = Stopwatch.StartNew();
    var amplified = Parse(bomb + "\n");
    Check($"a token wall is capped ({watch.ElapsedMilliseconds} ms)", amplified.Count == 1 && amplified[0].Cut
        && amplified[0].Detail!.Length <= LogParse.MaxDetailChars + 40 && watch.ElapsedMilliseconds < 2000);

    watch.Restart();
    var open = Parse("""{"@t":"2026-10-23T10:00:00Z","@mt":""" + "\"" + string.Concat(Enumerable.Repeat("{a", 30_000)) + "\"}\n");
    Check($"unclosed braces render in linear time ({watch.ElapsedMilliseconds} ms)", open.Count == 1 && watch.ElapsedMilliseconds < 2000);

    var junk = new StringBuilder("2026-10-23 10:00:00 [INF] a\n");
    for (var i = 0; i < 200_000; i++) junk.Append("{x}\n");
    watch.Restart();
    var many = Parse(junk.ToString());
    Check($"broken JSON lines stop being parsed ({watch.ElapsedMilliseconds} ms)", many.Count == 1 && many[0].Cut && watch.ElapsedMilliseconds < 3000);

    var sneaky = Parse("2026-10-23 10:00:00 [INF] Paid\rX" + (char)0x202E + "lave\n");
    Check("control and bidi characters are shown, not obeyed",
        sneaky.Count == 1 && !sneaky[0].Message.Contains('\r') && !sneaky[0].Message.Contains((char)0x202E)
        && sneaky[0].Message.Contains((char)0xFFFD) && sneaky[0].FirstLine.Contains((char)0x202E));
}
{
    var hostile = new StringBuilder();
    for (var i = 0; i < 100; i++)
    {
        hostile.Append("2026-10-23 10:00:00 ").Append('[', 60_000).Append('\n');
        hostile.Append(string.Concat(Enumerable.Repeat("12:", 20_000))).Append('\n');
        hostile.Append('{').Append(string.Concat(Enumerable.Repeat("\"a\":", 15_000))).Append('\n');
    }

    var watch = Stopwatch.StartNew();
    var e = Parse(hostile.ToString());
    Check($"hostile lines parse quickly ({watch.ElapsedMilliseconds} ms)", watch.ElapsedMilliseconds < 3000 && e.Count > 0);
}

// --- files ---
var dir = Path.Combine(scratch, "files");
Directory.CreateDirectory(dir);

string WriteBytes(string name, byte[] bytes)
{
    var path = Path.Combine(dir, name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
    return path;
}

byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

{
    var viet = "2026-10-23 10:00:00 [INF] Đã ký hợp đồng số 5\r\n";
    const string expected = "Đã ký hợp đồng số 5";

    var cases = new (string Name, byte[] Bytes)[]
    {
        ("utf8-bom.txt", Concat(Encoding.UTF8.GetPreamble(), Encoding.UTF8.GetBytes(viet))),
        ("utf8.txt", Encoding.UTF8.GetBytes(viet)),
        ("utf16le-bom.txt", Concat(Encoding.Unicode.GetPreamble(), Encoding.Unicode.GetBytes(viet))),
        ("utf16le.txt", Encoding.Unicode.GetBytes(viet + viet)),
        ("utf16be-bom.txt", Concat(Encoding.BigEndianUnicode.GetPreamble(), Encoding.BigEndianUnicode.GetBytes(viet))),
    };

    foreach (var (name, bytes) in cases)
    {
        var read = LogFile.Read(WriteBytes(name, bytes), name);
        Check($"encoding {name}", read.Problem is null && read.Entries.Count >= 1 && read.Entries[0].Message == expected);
    }

    var padded = LogFile.Read(WriteBytes("padded.txt", Concat(Encoding.UTF8.GetBytes(viet), new byte[5000])), "padded.txt");
    Check("zero padding is ignored", padded.Entries.Count == 1 && padded.Entries[0].Detail is null);

    var missing = LogFile.Read(Path.Combine(dir, "nope.txt"), "nope.txt");
    Check("a missing file is a problem, not a throw", missing.Problem == "The file has gone." && missing.Entries.Count == 0);

    var dated = LogFile.Read(WriteBytes("log-20261023.txt", Encoding.UTF8.GetBytes("[10:15:30 INF] Started\n")), "logInfor/log-20261023.txt");
    Check("time-only lines take the file's date", dated.Entries.Count == 1 && dated.Entries[0].Time?.Date == new DateTime(2026, 10, 23));
}
{
    var text = new StringBuilder();
    for (var i = 0; i < 1000; i++)
        text.Append($"2026-10-23 10:{i / 60 % 60:00}:{i % 60:00} [INF] entry {i}\n   at continuation {i}\n");
    var path = WriteBytes("many.txt", Encoding.UTF8.GetBytes(text.ToString()));

    var tail = LogFile.Read(path, "many.txt", maxBytes: 5000);
    Check("tail window: head skipped", tail.SkippedBytes > 0 && tail.Entries.Count is > 0 and < 1000);
    Check("tail window: no orphaned continuation", tail.Entries.All(x => x.Message.StartsWith("entry ")));
    Check("tail window: ends at the end", tail.Entries[^1].Message == "entry 999");

    var capped = LogFile.Read(path, "many.txt", maxEntries: 100);
    Check("entry cap keeps the newest", capped.Entries.Count == 100 && capped.Dropped == 900
        && capped.Entries[0].Message == "entry 900" && capped.Entries[^1].Message == "entry 999");

    var wide = LogFile.Read(WriteBytes("wide.txt", Encoding.UTF8.GetBytes("2026-10-23 10:00:00 [INF] " + new string('x', 100_000) + "\nnext\n")), "wide.txt");
    Check("a long line is cut and marked", wide.Entries.Count == 1 && wide.Entries[0].FirstLine.Length == LogFile.MaxLineChars + 1
        && wide.Entries[0].FirstLine.EndsWith("…")
        && wide.Entries[0].Message.Length == LogParse.MaxMessageChars + 1 && wide.Entries[0].Detail == "next");

    var heavy = new StringBuilder();
    for (var i = 0; i < 100; i++) heavy.Append($"2026-10-23 10:00:{i % 60:00} [INF] e{i} ").Append('x', 1000).Append('\n');
    var chars = LogFile.Read(WriteBytes("heavy.txt", Encoding.UTF8.GetBytes(heavy.ToString())), "heavy.txt", maxChars: 20_000);
    Check("the character cap keeps the newest", chars.Entries.Count is > 0 and < 20 && chars.Dropped == 100 - chars.Entries.Count
        && chars.Entries[^1].Message.StartsWith("e99 ") && chars.Entries.Sum(LogFile.Size) <= 20_000);
}
{
    Check("date in name: compact", LogFile.DateIn("log-20261023.txt") == new DateOnly(2026, 10, 23));
    Check("date in name: dashed", LogFile.DateIn("app_2026-10-23.log") == new DateOnly(2026, 10, 23));
    Check("date in name: under a folder", LogFile.DateIn("logInfor/log-20261023.txt") == new DateOnly(2026, 10, 23));
    Check("date in name: not part of a longer number", LogFile.DateIn("x1202610230.txt") is null);
    Check("date in name: impossible day", LogFile.DateIn("log-20260231.txt") is null);
}
{
    var root = Path.Combine(scratch, "listing");
    void Touch(string rel, int minutesAgo)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x\n");
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    Touch("a.txt", 5);
    Touch("b.LOG", 1);
    Touch("c.json", 3);
    Touch("d.dll", 0);
    Touch(".hidden.txt", 0);
    Touch("sub/e.txt", 2);
    Touch("sub/s2/s3/f.txt", 4);
    Touch("sub/s2/s3/s4/g.txt", 4);

    var outside = Path.Combine(scratch, "outside");
    Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "secret.txt"), "x\n");
    Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);

    var names = LogFile.List(root, LogSourceRules.Patterns(null)).Select(f => Path.GetRelativePath(root, f.FullName).Replace('\\', '/')).ToList();
    Check($"listing: newest first, patterns, case ({string.Join(",", names)})",
        names.SequenceEqual(["b.LOG", "sub/e.txt", "c.json", "sub/s2/s3/f.txt", "a.txt"]));
    Check("listing: other extensions skipped", !names.Contains("d.dll"));
    Check("listing: hidden files skipped", !names.Contains(".hidden.txt"));
    Check("listing: three levels down", names.Contains("sub/s2/s3/f.txt"));
    Check("listing: not four", !names.Contains("sub/s2/s3/s4/g.txt"));
    Check("listing: symlinks are not followed", !names.Any(n => n.Contains("secret")));
}

// --- the monitor ---
{
    var root = Path.Combine(scratch, "watch");
    Directory.CreateDirectory(root);
    var sources = new List<LogSourceEntry> { new() { Name = "w", Path = root, Files = "*.txt" } };
    var monitor = new LogMonitor(() => sources, () => 5);
    var raised = 0;
    monitor.Refreshed += (_, _) => raised++;

    var stamp = DateTime.UtcNow.AddHours(-1);
    void Put(string name, string text)
    {
        var p = Path.Combine(root, name);
        File.WriteAllText(p, text);
        stamp = stamp.AddSeconds(10);
        File.SetLastWriteTimeUtc(p, stamp);
    }

    Check("monitor: first sweep is a change", monitor.Sweep());
    Check("monitor: empty folder", monitor.View(root) is { Problem: null, Files.Count: 0, Entries.Count: 0 });

    Put("a.txt", "2026-10-23 10:00:00 [INF] a1\n2026-10-23 10:00:02 [ERR] a2\n");
    Check("monitor: a new file is a change", monitor.Sweep());
    var view = monitor.View(root)!;
    Check("monitor: its entries", view.Entries.Count == 2 && view.Errors == 1);

    Check("monitor: nothing changed", !monitor.Sweep() && ReferenceEquals(view, monitor.View(root)));

    Put("a.txt", "2026-10-23 10:00:00 [INF] a1\n2026-10-23 10:00:02 [ERR] a2\n2026-10-23 10:00:04 [WRN] a3\n");
    Check("monitor: a grown file is re-read", monitor.Sweep() && monitor.View(root)!.Entries.Count == 3);

    Put("b.txt", "2026-10-23 09:00:00 [INF] b1\n2026-10-23 10:00:03 [INF] b2\n");
    monitor.Sweep();
    var merged = monitor.View(root)!.Entries.Select(x => x.Message).ToArray();
    Check($"monitor: files merged by time ({string.Join(",", merged)})", merged.SequenceEqual(["b1", "a1", "a2", "b2", "a3"]));

    Put("ignored.log", "2026-10-23 10:00:00 [INF] no\n");
    Check("monitor: files outside the patterns don't count", !monitor.Sweep());

    File.Delete(Path.Combine(root, "a.txt"));
    Check("monitor: a deleted file goes", monitor.Sweep() && monitor.View(root)!.Entries.Select(x => x.Message).SequenceEqual(["b1", "b2"]));

    sources[0] = new LogSourceEntry { Name = "renamed", Path = root, Files = "*.txt" };
    Check("monitor: a renamed source is a change", monitor.Sweep() && monitor.View(root)!.Source.Name == "renamed");

    var gone = Path.Combine(scratch, "gone");
    sources.Add(new LogSourceEntry { Name = "g", Path = gone, Files = "*.txt" });
    Check("monitor: a missing folder", monitor.Sweep() && monitor.View(gone)?.Problem == "The folder doesn't exist.");
    Check("monitor: a missing folder doesn't repeat", !monitor.Sweep());

    var before = raised;
    sources.RemoveAt(1);
    Check("monitor: a removed source is a change", monitor.Sweep() && monitor.Views.Count == 1 && raised == before + 1);

    var inner = Path.Combine(root, "sub");
    Directory.CreateDirectory(inner);
    File.WriteAllText(Path.Combine(inner, "x.txt"), "2026-10-23 10:00:00 [INF] nested\n");
    sources.Add(new LogSourceEntry { Name = "inner", Path = inner, Files = "*.txt" });
    monitor.Sweep();
    var outerNames = monitor.View(root)!.Files.Select(f => f.Name.Replace('\\', '/')).ToList();
    var innerNames = monitor.View(inner)!.Files.Select(f => f.Name).ToList();
    Check($"monitor: nested sources name a file each their own way ({string.Join(",", outerNames)} / {string.Join(",", innerNames)})",
        outerNames.Contains("sub/x.txt") && innerNames.SequenceEqual(["x.txt"])
        && monitor.View(inner)!.Entries.Single().Source == "x.txt");
    Check("monitor: and the cache doesn't flip-flop between them", !monitor.Sweep());
}
{
    var root = Path.Combine(scratch, "budget");
    Directory.CreateDirectory(root);
    for (var f = 0; f < 6; f++)
    {
        var text = new StringBuilder();
        for (var i = 0; i < LogFile.MaxEntries; i++) text.Append($"2026-10-2{f} 10:00:00 [INF] f{f} {i}\n");
        var p = Path.Combine(root, $"log-{f}.txt");
        File.WriteAllText(p, text.ToString());
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(f - 10));
    }

    var monitor = new LogMonitor(() => [new LogSourceEntry { Name = "b", Path = root, Files = "*.txt" }], () => 5);
    monitor.Sweep();
    var view = monitor.Views[0];
    Check("budget: entries capped per folder", view.Entries.Count == LogFile.MaxFolderEntries);
    Check("budget: the oldest file is listed, not read", view.Files[^1] is { Name: "log-0.txt", OverBudget: true, Entries.Count: 0 });
    Check("budget: newest entries kept", view.Entries[^1].Message.StartsWith("f5 "));

    Check("budget: nothing trimmed when the files fit", view.Trimmed == 0);

    var over = Path.Combine(scratch, "overshoot");
    Directory.CreateDirectory(over);
    for (var f = 0; f < 8; f++)
    {
        var text = new StringBuilder();
        for (var i = 0; i < 7000; i++) text.Append($"2026-10-1{f} 10:00:00 [INF] g{f} {i}\n");
        var p = Path.Combine(over, $"log-{f}.txt");
        File.WriteAllText(p, text.ToString());
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(f - 10));
    }

    var shoot = new LogMonitor(() => [new LogSourceEntry { Name = "o", Path = over, Files = "*.txt" }], () => 5);
    shoot.Sweep();
    Check("budget: the last file over the line is trimmed, and says so",
        shoot.Views[0].Entries.Count == LogFile.MaxFolderEntries && shoot.Views[0].Trimmed == 8 * 7000 - LogFile.MaxFolderEntries);

    var lean = new LogMonitor(() => [new LogSourceEntry { Name = "b", Path = root, Files = "*.txt" }], () => 5,
                              folderBytes: new FileInfo(Path.Combine(root, "log-5.txt")).Length + 1);
    lean.Sweep();
    var files = lean.Views[0].Files;
    Check("budget: bytes cap the files read too", files[0] is { Name: "log-5.txt", OverBudget: false }
        && files[1] is { Name: "log-4.txt", OverBudget: false } && files.Skip(2).All(f => f.OverBudget));
}

// --- the config ---
{
    LogFolderCheck I(string raw) => LogSourceRules.Inspect(raw);

    Check("folder: quotes and slashes tidied", I("\"d:/Logs//eContract/\"").Folder == @"D:\Logs\eContract");
    Check("folder: a share is refused", I(@"\\server\share\logs").Problem?.Contains("Network shares") == true);
    Check("folder: a comma is refused", I(@"D:\Logs,foo").Folder is null);
    Check("folder: an NT path is refused", I(@"\\?\D:\logs").Folder is null);
    Check("folder: relative is refused", I("logs").Problem?.Contains("drive letter") == true && I("D:logs").Folder is null);
    Check("folder: a drive root is refused", I(@"D:\").Problem?.Contains("whole drive") == true);
    Check("folder: a stream is refused", I(@"D:\logs:x").Folder is null);
    Check("folder: wildcards are refused", I(@"D:\logs\*").Folder is null);
    Check("folder: dot steps are refused", I(@"D:\a\..\b").Folder is null && I(@"D:\a\.\b").Folder is null);
    Check("folder: a workspace path is explained", I("/home/coder/logs").Problem?.Contains("workspace path") == true);
    Check("folder: control characters are refused", I("D:\\lo\u0007gs").Folder is null);
    Check("folder: nothing", I("  ").Problem == "Pick a folder.");

    var kept = LogSourceRules.Normalize(
    [
        null,
        new LogSourceEntry { Path = @"D:\Logs\A" },
        new LogSourceEntry { Path = "d:/logs/a/" },
        new LogSourceEntry { Path = "" },
        new LogSourceEntry { Kind = "sqlserver", Path = @"D:\X" },
        new LogSourceEntry { Name = "  Live\u0007  ", Path = @"D:\Logs\B", Files = "*.log; *.txt;;a/b;*.LOG" },
    ]);
    Check("normalize: usable, unique folders only", kept.Length == 2);
    Check("normalize: a name from the folder", kept[0].Name == "A" && kept[0].Files == LogSourceRules.DefaultFiles);
    Check("normalize: name and patterns cleaned", kept[1].Name == "Live" && kept[1].Files == "*.log;*.txt");
    Check("normalize: capped", LogSourceRules.Normalize(Enumerable.Range(0, 30).Select(i => new LogSourceEntry { Path = $@"D:\L{i}" })).Length
        == LogSourceRules.MaxSources);

    Directory.CreateDirectory(CoderConfig.Dir);
    File.WriteAllText(CoderConfig.Path_,
        """{"logSources":[{"name":"x","path":"D:/L/","files":"*.txt"},{"path":"\\\\srv\\s"}],"logPollSeconds":1}""");
    var cfg = CoderConfig.Load();
    Check("config: sources normalised on load", cfg.LogSources.Length == 1 && cfg.LogSources[0].Path == @"D:\L");
    Check("config: poll clamped", cfg.LogPollSeconds == 2);
    Check("config: a table is polled every five minutes by default", cfg.LogTablePollMinutes == 5);

    // The build that polled every 60 seconds wrote that into every config; it doesn't outlive the new default.
    File.WriteAllText(CoderConfig.Path_, """{"logTablePollSeconds":60}""");
    Check("config: the old seconds setting is left behind", CoderConfig.Load().LogTablePollMinutes == 5);
    File.WriteAllText(CoderConfig.Path_, """{"logTablePollMinutes":0}""");
    Check("config: table poll clamped", CoderConfig.Load().LogTablePollMinutes == 1);
    File.WriteAllText(CoderConfig.Path_, """{"logTablePollMinutes":600}""");
    Check("config: and at most an hour", CoderConfig.Load().LogTablePollMinutes == 60);

    cfg.SetLogSources([new LogSourceEntry { Name = "y", Path = @"E:\Y" }]);
    var json = JsonSerializer.Serialize(cfg);
    Check("config: written under its names", json.Contains("\"logSources\"") && json.Contains("\"logPollSeconds\"")
        && json.Contains("E:\\\\Y") && !json.Contains("\"Id\"") && !json.Contains("\"Patterns\""));
}

// --- tables: the SQL text ---
{
    Check("quote doubles a closing bracket", LogSql.Quote("a]b") == "[a]]b]");
    Check("table: schema.table", LogSql.SplitTable("dbo.Logs") == ("dbo", "Logs"));
    Check("table: bare name is dbo", LogSql.SplitTable(" Logs ") == ("dbo", "Logs"));
    Check("table: bracketed with an escaped bracket", LogSql.SplitTable("[dbo].[Log]]s]") == ("dbo", "Log]s"));
    Check("table: a dot inside brackets", LogSql.SplitTable("[my.schema].[t]") == ("my.schema", "t"));
    Check("table: three parts, empty parts and an open bracket are refused",
        LogSql.SplitTable("a.b.c") is null && LogSql.SplitTable("dbo.") is null && LogSql.SplitTable("") is null
        && LogSql.SplitTable("[unclosed") is null);

    TableColumn[] serilog =
    [
        new("Id", "int"), new("Message", "nvarchar"), new("MessageTemplate", "nvarchar"), new("Level", "nvarchar"),
        new("TimeStamp", "datetime"), new("Exception", "nvarchar"), new("Properties", "xml"), new("LogEvent", "nvarchar"),
    ];

    var shape = LogSql.Resolve("dbo", "Logs", new LogColumnMap { Id = "id", Time = "timestamp", Message = "MESSAGE" }, serilog, idUsable: true);
    Check("resolve: the catalog's spelling", shape.Columns.Id == "Id" && shape.Columns.Time == "TimeStamp"
        && shape.Columns.Message == "Message" && shape.ById);

    var missing = Try(() => LogSql.Resolve("dbo", "Logs", new LogColumnMap { Message = "Msg" }, serilog));
    Check("resolve: a column that isn't there is said", missing?.Contains("\"Msg\"") == true);
    Check("resolve: no table", Try(() => LogSql.Resolve("dbo", "Nope", null, []))?.Contains("no table") == true);

    var first = LogSql.Select(shape, after: false);
    var next = LogSql.Select(shape, after: true);
    Check("select: newest by id", first.Contains("TOP (@n)") && first.Contains("WITH (NOLOCK)") && first.EndsWith("ORDER BY [Id] DESC")
        && !first.Contains("@after"));
    Check("select: after the cursor", next.Contains("WHERE [Id] > @after ORDER BY [Id] ASC"));
    Check("select: long columns cut on the server", first.Contains("LEFT(CAST([Message] AS nvarchar(max)), 8000) AS k_message")
        && first.Contains("NULL AS k_exception"));

    var byTime = LogSql.Resolve("dbo", "Logs", new LogColumnMap { Time = "TimeStamp", Message = "Message" }, serilog, timeIndexed: true);
    Check("select: without an id, newest by time", LogSql.Select(byTime, after: true).EndsWith("ORDER BY [TimeStamp] DESC")
        && !LogSql.Select(byTime, after: true).Contains("@after"));

    var noOrder = LogSql.Resolve("dbo", "Logs", new LogColumnMap { Message = "Message" }, serilog);
    Check("select: neither id nor time is refused", Try(() => LogSql.Select(noOrder, false))?.Contains("id or a time") == true);

    var timeOnly = LogSql.Resolve("dbo", "Logs", new LogColumnMap { Id = "Id", Time = "TimeStamp" }, serilog, idUsable: false, timeIndexed: true);
    Check("select: an id that isn't an identity or unique index is not counted from",
        !timeOnly.ById && LogSql.Select(timeOnly, true).EndsWith("ORDER BY [TimeStamp] DESC"));
    Check("select: neither indexed is refused", Try(() => LogSql.Select(
        LogSql.Resolve("dbo", "Logs", new LogColumnMap { Id = "Id", Time = "TimeStamp" }, serilog), false))?.Contains("scan the whole table") == true);
    Check("select: a view is trusted", LogSql.Resolve("dbo", "vLogs", new LogColumnMap { Id = "Id" }, serilog, isView: true).ById);

    TableColumn[] texty = [new("Id", "uniqueidentifier"), new("Created", "varchar"), new("Message", "nvarchar")];
    Check("select: a text time can't order the newest", Try(() => LogSql.Select(
        LogSql.Resolve("dbo", "T", new LogColumnMap { Id = "Id", Time = "Created" }, texty, timeIndexed: true), false))?.Contains("text") == true);

    TableColumn[] scaled = [new("Id", "decimal", 2), new("TimeStamp", "datetime")];
    Check("select: a decimal(18,2) id doesn't count up", !LogSql.Resolve("dbo", "T", new LogColumnMap { Id = "Id", Time = "TimeStamp" }, scaled, idUsable: true, timeIndexed: true).ById
        && LogSql.Resolve("dbo", "T", new LogColumnMap { Id = "Id" }, [new("Id", "decimal", 0)], idUsable: true).ById);

    Check("display: plain names plain, awkward ones quoted",
        LogSql.Display("dbo", "Logs") == "dbo.Logs" && LogSql.Display("dbo", "Log.Archive") == "dbo.[Log.Archive]"
        && LogSql.SplitTable(LogSql.Display("dbo", "Log.Archive")) == ("dbo", "Log.Archive")
        && LogSql.SplitTable(LogSql.Display("a]b", "c")) == ("a]b", "c"));

    TableColumn[] hostile = [new("Id", "bigint"), new("x]; DROP TABLE t;--", "nvarchar"), new("When", "datetime2")];
    var evil = LogSql.Select(LogSql.Resolve("s]x", "t]y", new LogColumnMap { Id = "Id", Time = "When", Message = "x]; DROP TABLE t;--" }, hostile, idUsable: true), true);
    var outside = System.Text.RegularExpressions.Regex.Replace(evil, @"\[(?:[^\]]|\]\])*\]", "[]");
    Check($"select: hostile names stay inside their brackets ({evil})",
        evil.Contains("[x]]; DROP TABLE t;--]") && evil.Contains("[s]]x].[t]]y]") && !outside.Contains("DROP") && !outside.Contains(";"));

    // Pages: newest first, below the last row of the page above, the level and the search done by the server.
    var page = LogSql.Page(shape, before: false, search: false, LogSeverity.Unknown);
    Check("page: the newest by id", page.Contains("TOP (@n)") && page.Contains("WITH (NOLOCK)")
        && page.EndsWith(" FROM [dbo].[Logs] WITH (NOLOCK) ORDER BY [Id] DESC"));
    Check("page: below the page above", LogSql.Page(shape, true, false, LogSeverity.Unknown).EndsWith("WHERE [Id] < @before ORDER BY [Id] DESC"));

    var leveled = LogSql.Resolve("dbo", "Logs",
        new LogColumnMap { Id = "Id", Level = "level", Message = "Message", Exception = "Exception", Properties = "Properties" }, serilog, idUsable: true);
    var errorPage = LogSql.Page(leveled, true, true, LogSeverity.Error);
    Check($"page: errors by name, in the casings services write ({errorPage})",
        errorPage.Contains("[Level] IN (N'") && errorPage.Contains("N'Error'") && errorPage.Contains("N'ERROR'") && errorPage.Contains("N'fatal'")
        && errorPage.Contains("N'Critical'") && errorPage.Contains("N'70000'") && !errorPage.Contains("N'Information'") && !errorPage.Contains("N'warn'"));
    Check("page: the search is a parameter, over every text column",
        errorPage.Contains("WHERE [Id] < @before AND [Level] IN (")
        && errorPage.Contains(" AND (CAST([Message] AS nvarchar(max)) LIKE @q ESCAPE N'\\' OR CAST([Exception] AS nvarchar(max)) LIKE @q ESCAPE N'\\' "
                              + "OR CAST([Properties] AS nvarchar(max)) LIKE @q ESCAPE N'\\') ORDER BY [Id] DESC"));
    Check("page: a filtered page runs on one core, a plain one as it is",
        errorPage.EndsWith(" ORDER BY [Id] DESC OPTION (MAXDOP 1)") && !page.Contains("MAXDOP")
        && LogSql.Page(byTime, false, true, LogSeverity.Unknown, ties: true).EndsWith(") OPTION (MAXDOP 1)"));

    TableColumn[] numbered = [new("Id", "bigint"), new("Level", "int"), new("Message", "nvarchar")];
    var byNumber = LogSql.Page(LogSql.Resolve("dbo", "T", new LogColumnMap { Id = "Id", Level = "Level", Message = "Message" }, numbered, idUsable: true),
        false, false, LogSeverity.Error);
    Check($"page: a number level by range ({byNumber})",
        byNumber.Contains("WHERE ([Level] BETWEEN 4 AND 5 OR [Level] BETWEEN 41 AND 999 OR [Level] >= 70000) ORDER BY") && !byNumber.Contains("N'"));

    TableColumn[] wide = [new("Id", "bigint"), new("Level", "ntext")];
    Check("page: an ntext level is compared as nvarchar (= refuses ntext)", LogSql.Page(
        LogSql.Resolve("dbo", "T", new LogColumnMap { Id = "Id", Level = "Level" }, wide, idUsable: true), false, false, LogSeverity.Error)
        .Contains("WHERE CAST([Level] AS nvarchar(64)) IN (N'"));

    TableColumn[] variant = [new("Id", "bigint"), new("Level", "sql_variant")];
    Check("page: any other level type is compared as text", LogSql.Page(
        LogSql.Resolve("dbo", "T", new LogColumnMap { Id = "Id", Level = "Level" }, variant, idUsable: true), false, false, LogSeverity.Warning)
        .Contains("WHERE CAST([Level] AS nvarchar(64)) IN (N'"));
    Check("page: no level column lets nothing through a floor", LogSql.Page(shape, false, false, LogSeverity.Info).Contains("WHERE 1 = 0 ORDER BY"));
    Check("page: nothing to search finds nothing", LogSql.Page(
        LogSql.Resolve("dbo", "Logs", new LogColumnMap { Id = "Id" }, serilog, idUsable: true), false, true, LogSeverity.Unknown).Contains("WHERE 1 = 0 ORDER BY"));

    Check("page: by time, below the last time", LogSql.Page(byTime, true, false, LogSeverity.Unknown).EndsWith("WHERE [TimeStamp] < @before ORDER BY [TimeStamp] DESC"));
    var tiePage = LogSql.Page(byTime, false, true, LogSeverity.Unknown, ties: true);
    Check($"page: the rows at the boundary time, all of them ({tiePage})",
        tiePage.Contains("WHERE [TimeStamp] = @tie AND (CAST([Message]") && !tiePage.Contains("ORDER BY") && !tiePage.Contains("@before"));

    var tiesById = false;
    try { LogSql.Page(shape, false, false, LogSeverity.Unknown, ties: true); }
    catch (InvalidOperationException) { tiesById = true; }
    Check("page: ties are only for a table read by time", tiesById);
    Check("page: a table that would be scanned is refused here too", Try(() => LogSql.Page(
        LogSql.Resolve("dbo", "Logs", new LogColumnMap { Id = "Id", Time = "TimeStamp" }, serilog), false, false, LogSeverity.Unknown))?.Contains("scan the whole table") == true);

    var evilPage = LogSql.Page(LogSql.Resolve("s]x", "t]y",
        new LogColumnMap { Id = "Id", Time = "When", Message = "x]; DROP TABLE t;--", Level = "x]; DROP TABLE t;--" }, hostile, idUsable: true),
        true, true, LogSeverity.Error);
    var evilOutside = Regex.Replace(Regex.Replace(evilPage, @"\[(?:[^\]]|\]\])*\]", "[]"), "N'(?:[^']|'')*'", "N''");
    Check($"page: hostile names stay inside their brackets ({evilPage})", !evilOutside.Contains("DROP") && !evilOutside.Contains(";") && !evilOutside.Contains("--"));

    Check("like: found anywhere, as written", LogSql.Like(@"50%_[a]\b") == @"%50\%\_\[a]\\b%");
    Check("search term: trimmed, and blank is none",
        LogTableRows.SearchTerm("  x y ") == "x y" && LogTableRows.SearchTerm("   ") is null && LogTableRows.SearchTerm(null) is null);
    Check("search term: control characters dropped (a NUL can't go into a BSON pattern)",
        LogTableRows.SearchTerm("a" + (char)0 + "b" + (char)9) == "ab" && LogTableRows.SearchTerm("" + (char)0) is null);
    var longTerm = new string('a', LogTableRows.MaxSearchChars - 1) + char.ConvertFromUtf32(0x1F600) + "tail";
    Check("search term: cut to size, never between the halves of a pair",
        LogTableRows.SearchTerm(longTerm) is { Length: LogTableRows.MaxSearchChars - 1 } cutTerm && !char.IsHighSurrogate(cutTerm[^1]));
}

// --- tables: the levels a server filters by ---
{
    long[] samples = [.. Enumerable.Range(-2, 1200).Select(n => (long)n), .. Enumerable.Range(0, 600).Select(i => i * 250L), long.MaxValue / 2];
    foreach (var floor in new[] { LogSeverity.Trace, LogSeverity.Info, LogSeverity.Warning, LogSeverity.Error, LogSeverity.Fatal })
    {
        var ranges = LogParse.LevelNumbersFrom(floor);
        var wrong = samples.Where(n => (LogParse.LevelOf(n) >= floor) != ranges.Any(r => n >= r.Low && (r.High is not { } high || n <= high))).Take(3).ToList();
        Check($"levels: the ranges are the numbers that read as {floor} or worse ({string.Join(",", wrong)})", wrong.Count == 0);
        Check($"levels: every name for {floor} or worse reads so", LogParse.LevelNamesFrom(floor).All(n => LogParse.LevelOf(n) >= floor));
    }

    var infoUp = LogParse.LevelNamesFrom(LogSeverity.Info);
    Check("levels: the casings services write, and the usual numbers",
        infoUp.Contains("Information") && infoUp.Contains("INFO") && infoUp.Contains("info") && infoUp.Contains("Warning") && infoUp.Contains("40000")
        && infoUp.Contains("2") && !infoUp.Contains("Debug") && !infoUp.Contains("DEBUG") && !infoUp.Contains("1"));
}

// --- tables: MongoDB filters ---
{
    BsonDocument Render(FilterDefinition<BsonDocument> filter) =>
        filter.Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));

    var level = Render(MongoLogReader.LevelFilter("Level", LogSeverity.Error));
    var any = level["$or"].AsBsonArray;
    var names = any[0]["Level"]["$in"].AsBsonArray.Select(v => v.AsString).ToList();
    var ranges = any.Skip(1).Select(x => x["Level"].AsBsonDocument)
        .Select(d => (d["$gte"].ToInt64(), d.Contains("$lte") ? d["$lte"].ToInt64() : (long?)null)).ToList();
    Check($"mongo level: names in each casing, numbers by range ({level})",
        names.Contains("Error") && names.Contains("ERROR") && names.Contains("error") && names.Contains("Fatal") && names.Contains("50") && !names.Contains("Warning")
        && ranges.SequenceEqual([(4L, (long?)5L), (41L, 999L), (70000L, null)]));

    var search = Render(MongoLogReader.SearchFilter(
        new LogColumnMap { Id = "_id", Message = "RenderedMessage", Exception = "Exception", Logger = "Properties.SourceContext" }, "a.b(c)*"));
    var either = search["$or"].AsBsonArray.Select(x => x.AsBsonDocument).ToList();
    Check($"mongo search: each text field, ignoring case, as written ({search})", either.Count == 3
        && either.All(x => x.ElementCount == 1 && x.GetElement(0).Value is BsonRegularExpression { Options: "i", Pattern: @"a\.b\(c\)\*" }));
    Check("mongo search: nothing to search finds nothing",
        Render(MongoLogReader.SearchFilter(new LogColumnMap { Time = "T" }, "x")).ToString() == "{ \"_id\" : { \"$in\" : [] } }");

    var vietnamese = "l" + (char)0x1ED7 + "i";
    string[] terms = ["50% off", @"a\b", "^$.|?*+()[]{}", "x]y", "(?i)", "#c -d", vietnamese + " " + char.ConvertFromUtf32(0x1F600), "/x/"];
    Check("mongo search: every term matches only itself",
        terms.All(t => Regex.IsMatch(t, "^" + MongoLogReader.Literal(t) + "$") && !Regex.IsMatch(t + "!", "^" + MongoLogReader.Literal(t) + "$")));
    Check("mongo search: letters and pairs are left alone", MongoLogReader.Literal(vietnamese + char.ConvertFromUtf32(0x1F600)) == vietnamese + char.ConvertFromUtf32(0x1F600));
}

// --- tables: guessing the columns ---
{
    LogColumnMap G(params (string Name, string Type)[] columns) => LogTableRows.Guess(columns.Select(c => new TableColumn(c.Name, c.Type)).ToList());

    var serilog = G(("Id", "int"), ("Message", "nvarchar"), ("MessageTemplate", "nvarchar"), ("Level", "nvarchar"),
        ("TimeStamp", "datetime"), ("Exception", "nvarchar"), ("Properties", "xml"), ("LogEvent", "nvarchar"));
    Check("guess: Serilog MSSqlServer", serilog is { Id: "Id", Time: "TimeStamp", Message: "Message", Level: "Level",
        Exception: "Exception", Properties: "Properties", Logger: null });

    var log4net = G(("Id", "int"), ("Date", "datetime"), ("Thread", "varchar"), ("Level", "varchar"), ("Logger", "varchar"),
        ("Message", "varchar"), ("Exception", "varchar"));
    Check("guess: log4net", log4net is { Time: "Date", Logger: "Logger", Message: "Message" });

    var nlog = G(("Id", "int"), ("MachineName", "nvarchar"), ("Logged", "datetime"), ("Level", "nvarchar"), ("Message", "nvarchar"),
        ("Logger", "nvarchar"), ("Callsite", "nvarchar"), ("Exception", "nvarchar"));
    Check("guess: NLog", nlog is { Time: "Logged", Logger: "Logger" });

    var custom = G(("LogID", "bigint"), ("NgayTao", "datetime"), ("NoiDung", "nvarchar"), ("MucDo", "nvarchar"));
    Check("guess: a hand-made Vietnamese table", custom is { Id: "LogID", Time: "NgayTao", Message: "NoiDung", Level: "MucDo" });

    Check("guess: a guid id is not an id to count from", G(("Id", "uniqueidentifier"), ("Message", "nvarchar")).Id is null);

    var mongo = G(("_id", "ObjectId"), ("Timestamp", "DateTime"), ("Level", "String"), ("MessageTemplate", "String"),
        ("RenderedMessage", "String"), ("Properties", "Document"), ("UtcTimeStamp", "DateTime"));
    Check("guess: Serilog MongoDB", mongo is { Id: "_id", Time: "Timestamp", Message: "RenderedMessage", Properties: "Properties" });

    // eContract's dbo.UserActivityLog: snake_case, a guid id, an audit trail rather than a log.
    var audit = G(("id", "uniqueidentifier"), ("owner_id", "uniqueidentifier"), ("user_id", "uniqueidentifier"),
        ("user_fullname", "nvarchar"), ("action", "varchar"), ("entity_type", "varchar"), ("entity_id", "nvarchar"),
        ("changes", "nvarchar"), ("ip_address", "varchar"), ("success", "bit"), ("is_legal", "bit"), ("created_date", "datetime2"));
    Check($"guess: snake_case and an audit table ({audit.Time}, {audit.Message}, {audit.Properties})",
        audit is { Id: null, Time: "created_date", Message: "action", Properties: "changes", Level: null, Exception: null, Logger: null });

    var snake = G(("log_id", "bigint"), ("created_at", "datetime"), ("log_level", "varchar"), ("message", "nvarchar"), ("stack_trace", "nvarchar"));
    Check("guess: snake_case names", snake is { Id: "log_id", Time: "created_at", Level: "log_level", Message: "message", Exception: "stack_trace" });
    Check("guess: the name as written before the same name without underscores",
        G(("Log_Id", "int"), ("LogId", "int"), ("Message", "nvarchar")).Id == "LogId"
        && G(("Message", "nvarchar"), ("Action", "nvarchar")).Message == "Message");
}

// --- tables: rows into entries ---
{
    var clock = new DateTime(2026, 10, 23, 10, 0, 0);
    TableRow Row(object? time = null, object? level = null, string? message = "m", string? exception = null, string? properties = null) =>
        new("k", "1", 7, time ?? clock, level, message, exception, properties, null);

    var local = LogTableRows.ToEntry(Row(), "dbo.Logs", utc: false);
    var utc = LogTableRows.ToEntry(Row(), "dbo.Logs", utc: true);
    Check("row: a clock time is local unless the source says UTC",
        local.Time?.DateTime == clock && utc.Time == new DateTimeOffset(clock, TimeSpan.Zero));
    Check("row: an offset time stays", LogTableRows.ToEntry(Row(new DateTimeOffset(clock, TimeSpan.FromHours(7))), "t", true).Time?.Offset == TimeSpan.FromHours(7));
    Check("row: a text time honours UTC", LogTableRows.ToEntry(Row("2026-10-23 10:00:00"), "t", true).Time == new DateTimeOffset(clock, TimeSpan.Zero));

    LogSeverity L(object? level) => LogTableRows.ToEntry(Row(level: level), "t", false).Level;
    Check("row: level names", L("Information") == LogSeverity.Info && L("Warning") == LogSeverity.Warning && L("FATAL") == LogSeverity.Fatal);
    Check("row: log4net numbers, as text or not", L("40000") == LogSeverity.Info && L(70000) == LogSeverity.Error && L(110000L) == LogSeverity.Fatal);
    Check("row: MEL and pino numbers", L(4) == LogSeverity.Error && L(50) == LogSeverity.Error && L((short)2) == LogSeverity.Info);
    Check("row: no level", L(null) == LogSeverity.Unknown && L("whatever") == LogSeverity.Unknown);

    var multi = LogTableRows.ToEntry(Row(message: "first\r\nsecond", exception: "System.Exception: x"), "dbo.Logs", false);
    Check("row: first line is the message, the rest leads the detail",
        multi.Message == "first" && multi.Detail == "second\nSystem.Exception: x" && multi.Source == "dbo.Logs" && multi.Line == 7
        && multi.FirstLine.Contains("[Unknown] first"));

    var xml = LogTableRows.ToEntry(Row(properties: "<properties><property key='SourceContext'>MB.Sign</property><property key='RequestId'>5</property></properties>"), "t", false);
    Check("row: Serilog XML properties", xml.Detail?.Contains("SourceContext: MB.Sign") == true && xml.Detail.Contains("RequestId: 5"));

    var xxe = LogTableRows.ToEntry(Row(properties: "<!DOCTYPE p [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><properties><property key='a'>&e;</property></properties>"), "t", false);
    Check("row: a DTD is refused, never resolved", xxe.Detail?.Contains("root:") == false && xxe.Detail.Contains("<!DOCTYPE"));

    var json = LogTableRows.ToEntry(Row(properties: """{"Timestamp":"x","Properties":{"RequestPath":"/api/sign","Elapsed":12}}"""), "t", false);
    Check("row: a LogEvent column's properties", json.Detail?.Contains("RequestPath: /api/sign") == true && json.Detail.Contains("Elapsed: 12")
        && !json.Detail.Contains("Timestamp"));

    var big = LogTableRows.ToEntry(Row(exception: new string('e', 40_000)), "t", false);
    Check("row: detail capped", big.Cut && big.Detail!.Length <= LogParse.MaxDetailChars + 40);

    var ancient = LogTableRows.ToEntry(Row(time: DateTime.MinValue), "t", false);
    Check("row: 0001-01-01 is no time, not a crash", ancient.Time is null);

    var shady = LogTableRows.ToEntry(Row(message: "Paid" + (char)7 + "ok"), "t", false);
    Check("row: control characters shown, not obeyed", shady.Message.Contains((char)0xFFFD) && !shady.Message.Contains((char)7));
}

// --- tables: the config ---
{
    var kept = LogSourceRules.Normalize(
    [
        new LogSourceEntry { Kind = "sqlserver", Connection = "dpapi:abc", Host = "srv / db", Table = "dbo.Logs",
            Columns = new LogColumnMap { Message = "Message", Time = " TimeStamp " }, Utc = false },
        new LogSourceEntry { Kind = "sqlserver", Connection = "dpapi:other", Host = "srv / db", Table = "dbo.Logs" },
        new LogSourceEntry { Kind = "sqlserver", Connection = "dpapi:abc", Host = "srv / db" },
        new LogSourceEntry { Kind = "sqlserver", Table = "dbo.X" },
        new LogSourceEntry { Kind = "MONGO", Connection = "Server=x;Password=plain", Table = "logs", Database = "app",
            Columns = new LogColumnMap(), Utc = true },
    ]);

    Check("normalize tables: usable and unique", kept.Length == 2);
    Check("normalize tables: name, columns and utc tidied", kept[0] is { Name: "Logs", Kind: "sqlserver", Utc: null }
        && kept[0].Columns is { Time: "TimeStamp", Message: "Message" } && kept[0].Id == "sqlserver:srv / db//dbo.Logs");
    Check("normalize tables: kind lower-cased, empty columns dropped, host defaulted, plain text kept for the page to refuse",
        kept[1] is { Kind: "mongo", Columns: null, Host: "server", Utc: true, Connection: "Server=x;Password=plain" });

    var longHost = string.Join(",", Enumerable.Range(1, 12).Select(i => $"mongo-node-{i:00}.internal.example.com:27017")) + " / logs";
    var hosted = LogSourceRules.Normalize([new LogSourceEntry { Kind = "mongo", Connection = "dpapi:a", Table = "t", Host = longHost }]);
    Check("normalize tables: a long host label is cut, not replaced", hosted.Single().Host!.Length == 200 && longHost.StartsWith(hosted.Single().Host!));
    Check("normalize tables: a database name MongoDB would refuse is dropped",
        LogSourceRules.Normalize([new LogSourceEntry { Kind = "mongo", Connection = "dpapi:a", Table = "t", Database = "a.b" }]).Length == 0
        && LogSourceRules.DatabaseProblem("app_logs") is null);
    Check("a rename reads the same", kept[0].ReadsSameAs(new LogSourceEntry
    {
        Name = "other", Kind = kept[0].Kind, Connection = kept[0].Connection, Host = kept[0].Host, Table = kept[0].Table, Columns = kept[0].Columns,
    }) && !kept[0].SameAs(new LogSourceEntry { Name = "other", Kind = kept[0].Kind, Connection = kept[0].Connection, Host = kept[0].Host, Table = kept[0].Table, Columns = kept[0].Columns }));

    var crashed = CrashLog.Describe(new InvalidOperationException("outer", new MongoDB.Fake.FakeDriverException("mongodb://u:Secret-9@h")));
    Check("crash log: a driver's message is withheld", !crashed.Contains("Secret-9") && crashed.Contains("FakeDriverException"));
    Check("crash log: anything else is written as it is", CrashLog.Describe(new InvalidOperationException("plain")).Contains("plain"));

    var json = JsonSerializer.Serialize(new CoderConfig { LogSources = kept });
    Check("normalize tables: written under their names", json.Contains("\"connection\"") && json.Contains("\"table\"")
        && json.Contains("\"columns\"") && json.Contains("\"utc\":true") && !json.Contains("\"utc\":false"));
}

// --- tables: the monitor ---
{
    var opened = 0;
    var tables = new Dictionary<string, FakeTable>();
    var sources = new List<LogSourceEntry>();
    LogSourceEntry Table(string name, LogColumnMap? columns = null) =>
        new() { Name = name, Kind = LogSourceKinds.SqlServer, Connection = "dpapi:x", Host = "h", Table = name, Columns = columns };

    var monitor = new LogMonitor(() => sources, () => 5, openTable: s =>
    {
        opened++;
        if (s.Table == "plain") throw new LogTableException("This connection string is stored as plain text.");
        return tables[s.Table!];
    }, tablePollSeconds: () => 60);

    var now = new DateTime(2026, 10, 23, 3, 0, 0, DateTimeKind.Utc);
    bool Sweep(bool force = true, int seconds = 0) => monitor.SweepTablesAsync(force, now.AddSeconds(seconds)).GetAwaiter().GetResult();

    var logs = new FakeTable();
    logs.Add(1, 1500);
    tables["logs"] = logs;
    sources.Add(Table("logs"));

    Check("table: first read is a change", Sweep());
    var view = monitor.Views.Single();
    Check("table: the newest 1,000, oldest first", view.Entries.Count == LogMonitor.InitialRows
        && view.Entries[0].Message == "row 501" && view.Entries[^1].Message == "row 1500" && view.IsTable && view.Problem is null);
    Check("table: asked for the newest", logs.Calls.Single() == (null, LogMonitor.InitialRows));

    Check("table: not due yet", !Sweep(force: false, seconds: 30) && logs.Calls.Count == 1);
    Check("table: due after the poll, and an empty read checks the newest id", !Sweep(force: false, seconds: 61)
        && logs.Calls.Count == 3 && logs.Calls[1] == ("1500", LogMonitor.BatchRows) && logs.Calls[2] == (null, 1));

    logs.Add(1501, 1503);
    Check("table: new rows are added", Sweep() && monitor.Views.Single().Entries.Count == 1003 && monitor.Views.Single().Entries[^1].Message == "row 1503");

    var calls = logs.Calls.Count;
    logs.Add(1504, 2703);
    Sweep();
    Check("table: a backlog is read in batches", logs.Calls.Count == calls + 3 && monitor.Views.Single().Entries[^1].Message == "row 2703");

    view = monitor.Views.Single();
    Check("table: nothing new is no change", !Sweep() && ReferenceEquals(view, monitor.Views.Single()));

    logs.Overlap = true;
    logs.Add(2704, 2704);
    Sweep();
    Check("table: a row read twice is held once", monitor.Views.Single().Entries.Count(e => e.Message == "row 2703") == 1
        && monitor.Views.Single().Entries[^1].Message == "row 2704");
    logs.Overlap = false;

    logs.Fail = new LogTableException("SQL Server: Login failed for user 'reader'.");
    Check("table: a failed read keeps what was read", Sweep() && monitor.Views.Single() is { Problem: null, Stale: "SQL Server: Login failed for user 'reader'." }
        && monitor.Views.Single().Entries.Count > 0);
    Check("table: and doesn't repeat itself", !Sweep());

    logs.Fail = new InvalidOperationException("Server=x;Password=hunter2");
    Sweep();
    Check("table: an unexpected failure is said without its text", monitor.Views.Single().Stale == "The table couldn't be read."
        && !monitor.Views.Single().Stale!.Contains("hunter2"));

    logs.Fail = null;
    logs.Add(2705, 2705);
    Check("table: recovery clears the note", Sweep() && monitor.Views.Single().Stale is null);

    var openedBefore = opened;
    sources[0] = Table("logs", new LogColumnMap { Id = "Id", Message = "Message" });
    var fresh = new FakeTable();
    fresh.Add(1, 10);
    tables["logs"] = fresh;
    Check("table: a changed source starts again with a new reader", Sweep() && logs.Disposed && opened == openedBefore + 1
        && monitor.Views.Single().Entries.Count == 10);

    sources.Add(Table("plain"));
    Sweep();
    Check("table: a reader that can't open says why", monitor.Views[1] is { Problem: "This connection string is stored as plain text.", Entries.Count: 0 });

    sources.RemoveAt(0);
    Check("table: a removed source goes and its reader is closed", Sweep() && fresh.Disposed && monitor.Views.Single().Source.Table == "plain");

    var window = new FakeTable { Window = true };
    window.Add(1, 1200);
    tables["window"] = window;
    sources.Clear();
    sources.Add(Table("window"));
    Sweep();
    Check("table: window mode holds the newest", monitor.Views.Single().Entries.Count == LogMonitor.InitialRows
        && monitor.Views.Single().Entries[^1].Message == "row 1200" && window.Calls.All(c => c.After is null));
    Check("table: window mode, nothing new", !Sweep());
    window.Add(1201, 1201);
    Check("table: window mode, the newest move", Sweep() && monitor.Views.Single().Entries[^1].Message == "row 1201"
        && monitor.Views.Single().Entries.Count == LogMonitor.InitialRows);

    var long_ = new FakeTable();
    long_.Add(1, 1000);
    tables["long"] = long_;
    sources.Clear();
    sources.Add(Table("long"));
    Sweep();
    for (var i = 0; i < 6; i++)
    {
        // Just under four full batches a poll: read in full, never skipped.
        long_.Add(1001 + i * 1900, 1000 + (i + 1) * 1900);
        Sweep();
    }
    var capped = monitor.Views.Single();
    Check($"table: capped at {LogFile.MaxEntries} entries, oldest dropped and counted ({capped.Entries.Count}, {capped.Trimmed})",
        capped.Entries.Count == LogFile.MaxEntries && capped.Entries[0].Message == "row 2401" && capped.Trimmed == 2400
        && capped.Note is null);

    var busy = new FakeTable();
    busy.Add(1, 100);
    tables["busy"] = busy;
    sources.Clear();
    sources.Add(Table("busy"));
    Sweep();
    busy.Add(101, 10_100);
    Sweep();
    var behind = monitor.Views.Single();
    Check("table: a backlog it can't clear skips to the newest, and says so",
        behind.Entries[^1].Message == "row 10100" && behind.Note?.Contains("skipped") == true
        && behind.Entries.Count(e => e.Message == "row 2100") == 1 && behind.Entries.All(e => e.Message != "row 2101"));
    busy.Add(10_101, 10_101);
    Check("table: and carries on from there", Sweep() && monitor.Views.Single().Entries[^1].Message == "row 10101");

    var reset = new FakeTable();
    reset.Add(5000, 5010);
    tables["reset"] = reset;
    sources.Clear();
    sources.Add(Table("reset"));
    Sweep();
    reset.Clear();
    reset.Add(1, 3);
    Check("table: a table emptied and started again is read afresh", Sweep()
        && monitor.Views.Single().Entries.Select(e => e.Message).SequenceEqual(["row 1", "row 2", "row 3"])
        && monitor.Views.Single().Note?.Contains("afresh") == true);
    reset.Add(4, 4);
    Check("table: and follows the new ids", Sweep() && monitor.Views.Single().Entries[^1].Message == "row 4");

    // Reload asks for one table; it is read early, but not twice within the gap.
    var asked = new FakeTable();
    asked.Add(1, 5);
    tables["asked"] = asked;
    sources.Clear();
    sources.Add(Table("asked"));
    monitor.SweepTablesAsync(false, now).GetAwaiter().GetResult();
    var askedCalls = asked.Calls.Count;
    var id = sources[0].Id;
    monitor.SweepTablesAsync(false, now.AddSeconds(5), asked: [id]).GetAwaiter().GetResult();
    Check("table: asked again within 15 s is not read", asked.Calls.Count == askedCalls);
    monitor.SweepTablesAsync(false, now.AddSeconds(16), asked: [id]).GetAwaiter().GetResult();
    Check("table: asked after 15 s is read", asked.Calls.Count > askedCalls);
    monitor.SweepTablesAsync(false, now.AddSeconds(20), asked: ["sqlserver:h/other"]).GetAwaiter().GetResult();

    // A failing server is asked less and less often.
    var flaky = new FakeTable { Fail = new LogTableException("SQL Server: timeout") };
    tables["flaky"] = flaky;
    sources.Clear();
    sources.Add(Table("flaky"));
    var t0 = now.AddHours(1);
    monitor.SweepTablesAsync(false, t0).GetAwaiter().GetResult();
    monitor.SweepTablesAsync(false, t0.AddSeconds(61)).GetAwaiter().GetResult();
    var tries = flaky.Calls.Count;
    monitor.SweepTablesAsync(false, t0.AddSeconds(150)).GetAwaiter().GetResult();
    Check("table: after two failures the next try waits twice as long", flaky.Calls.Count == tries);
    monitor.SweepTablesAsync(false, t0.AddSeconds(182)).GetAwaiter().GetResult();
    Check("table: and then tries again", flaky.Calls.Count == tries + 1 && monitor.Views.Single().Problem == "SQL Server: timeout");

    // A new name is only a label.
    var named = new FakeTable();
    named.Add(1, 3);
    tables["named"] = named;
    sources.Clear();
    sources.Add(Table("named"));
    Sweep();
    var opens = opened;
    sources[0] = new LogSourceEntry { Name = "Renamed", Kind = LogSourceKinds.SqlServer, Connection = "dpapi:x", Host = "h", Table = "named" };
    Check("table: a rename keeps the reading going", Sweep() && opened == opens && !named.Disposed
        && monitor.Views.Single().Source.Name == "Renamed" && monitor.Views.Single().Entries.Count == 3);

    var heavyWindow = new FakeTable { Window = true, Text = "x\n" + new string('y', 12_000) };
    heavyWindow.Add(1, 1000);
    tables["heavyWindow"] = heavyWindow;
    sources.Clear();
    sources.Add(Table("heavyWindow"));
    Sweep();
    Check("table: a window cut by the size cap isn't taken for new rows every poll",
        monitor.Views.Single().Trimmed > 0 && !Sweep());

    var heavy = new FakeTable { Text = "x\n" + new string('y', 12_000) };
    heavy.Add(1, 1000);
    tables["heavy"] = heavy;
    sources.Clear();
    sources.Add(Table("heavy"));
    Sweep();
    var held = monitor.Views.Single();
    Check($"table: capped at {LogMonitor.MaxTableChars} characters ({held.Entries.Count} held)",
        held.Trimmed > 0 && held.Entries.Sum(LogFile.Size) <= LogMonitor.MaxTableChars);

    // A folder and a table, in the order the config lists them.
    var folder = Path.Combine(scratch, "mixed");
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "a.txt"), "2026-10-23 10:00:00 [INF] from a file\n");
    var small = new FakeTable();
    small.Add(1, 2);
    tables["small"] = small;
    sources.Clear();
    sources.Add(Table("small"));
    sources.Add(new LogSourceEntry { Name = "f", Path = folder, Files = "*.txt" });
    monitor.Sweep();
    Sweep();
    Check("table and folder: both shown, in config order", monitor.Views.Count == 2 && monitor.Views[0].IsTable && !monitor.Views[1].IsTable
        && monitor.Views[1].Entries.Single().Message == "from a file");

    // Pages for the Logs page: read when asked, by a reader of their own.
    var paged = new FakeTable();
    paged.Add(1, 250);
    tables["paged"] = paged;
    sources.Clear();
    sources.Add(Table("paged"));
    var pid = sources[0].Id;
    TablePageView Page(TablePageQuery query) => monitor.PageAsync(pid, query, CancellationToken.None).GetAwaiter().GetResult();

    var pageOpens = opened;
    var p1 = Page(new TablePageQuery(100));
    Check("page: newest first, one page's worth", p1.Problem is null && p1.Entries.Count == 100
        && p1.Entries[0].Line == 250 && p1.Entries[^1].Line == 151 && p1.Older is "151");
    var p2 = Page(new TablePageQuery(100, p1.Older));
    var p3 = Page(new TablePageQuery(100, p2.Older));
    Check("page: the older pages follow on, and the last says so", p2.Entries[0].Line == 150 && p2.Entries[^1].Line == 51
        && p3.Entries.Count == 50 && p3.Entries[^1].Line == 1 && p3.Older is null);
    Check("page: one reader for the pages, opened once", opened == pageOpens + 1 && paged.Pages.Count == 3);

    var filtered = Page(new TablePageQuery(10, null, LogSeverity.Error, "row 2"));
    var expected = Enumerable.Range(1, 250).Count(n => n % 10 == 0 && $"row {n}".Contains("row 2"));
    Check("page: the level and the search go to the reader", paged.Pages[^1] is { Floor: LogSeverity.Error, Search: "row 2", Size: 10 }
        && filtered.Entries.Count == expected && filtered.Entries.All(e => e.Level == LogSeverity.Error) && filtered.Older is null);

    paged.Fail = new LogTableException("SQL Server: Invalid column name 'Level'.");
    var failed = Page(new TablePageQuery(100));
    Check("page: a failure is said, and its reader closed", failed.Problem == "SQL Server: Invalid column name 'Level'."
        && failed.Entries.Count == 0 && failed.Older is null && paged.Disposed);

    var reopened = new FakeTable();
    reopened.Add(1, 250);
    tables["paged"] = reopened;
    Check("page: the next ask opens a fresh reader", Page(new TablePageQuery(5)).Entries.Count == 5 && opened == pageOpens + 2);

    reopened.Fail = new LogTableException(LogTableRows.SlowPage, slow: true);
    var slowPage = Page(new TablePageQuery(5));
    Check("page: a slow server is said as slow, and its reader kept", slowPage is { Slow: true, Problem: LogTableRows.SlowPage }
        && !reopened.Disposed && opened == pageOpens + 2);

    reopened.Fail = new InvalidOperationException("text from the server");
    Check("page: anything else is said in safe words", Page(new TablePageQuery(5)).Problem == "The page couldn't be read." && reopened.Disposed);
    reopened.Fail = null;
    Check("page: a reader closed under the page is said as such", Page(new TablePageQuery(5)).Problem?.Contains("settings changed") == true);

    var slow = new FakeTable { Gate = new TaskCompletionSource() };
    slow.Add(1, 10);
    tables["paged"] = slow;
    using (var cancel = new CancellationTokenSource())
    {
        var pending = monitor.PageAsync(pid, new TablePageQuery(5), cancel.Token);
        cancel.Cancel();
        var cancelled = false;
        try { pending.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelled = true; }
        Check("page: one no longer wanted is cancelled, its reader kept", cancelled && !slow.Disposed);
    }

    slow.Gate = null;
    Check("page: the kept reader answers the next ask", Page(new TablePageQuery(5)).Entries.Count == 5 && tables["paged"] == slow && !slow.Disposed);
    sources[0] = Table("paged", new LogColumnMap { Id = "Id", Message = "Msg" });
    Sweep(force: false, seconds: 1);
    Check("page: a table that now reads something else has its page reader closed", slow.Disposed);

    var idle = new FakeTable();
    idle.Add(1, 10);
    tables["idle"] = idle;
    sources.Add(Table("idle"));
    monitor.PageAsync(sources[^1].Id, new TablePageQuery(5), CancellationToken.None).GetAwaiter().GetResult();
    monitor.SweepTablesAsync(false, DateTime.UtcNow.AddMinutes(1)).GetAwaiter().GetResult();
    var keptAWhile = !idle.Disposed;
    monitor.SweepTablesAsync(false, DateTime.UtcNow.AddMinutes(6)).GetAwaiter().GetResult();
    Check("page: a page reader not used for five minutes is closed", keptAWhile && idle.Disposed);
    sources.RemoveAt(sources.Count - 1);

    Check("page: a table that's gone is said", monitor.PageAsync("sqlserver:h//nope", new TablePageQuery(5), CancellationToken.None)
        .GetAwaiter().GetResult().Problem?.Contains("isn't in the list") == true);
    sources[0] = Table("plain");
    Check("page: a connection the app won't open is said", monitor.PageAsync(sources[0].Id, new TablePageQuery(5), CancellationToken.None)
        .GetAwaiter().GetResult().Problem?.Contains("plain text") == true);

    var lastOne = new FakeTable();
    lastOne.Add(1, 3);
    tables["last"] = lastOne;
    sources[0] = Table("last");
    monitor.PageAsync(sources[0].Id, new TablePageQuery(5), CancellationToken.None).GetAwaiter().GetResult();
    var openedBeforeStop = opened;
    monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    Check("page: closing the monitor closes the page readers", lastOne.Disposed);
    var afterwards = monitor.PageAsync(sources[0].Id, new TablePageQuery(5), CancellationToken.None).GetAwaiter().GetResult();
    Check("page: none is read once the monitor has stopped", afterwards.Problem is not null && opened == openedBeforeStop);
}

try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }

Console.WriteLine(failures == 0 ? $"ALL PASS ({checks} checks)" : $"{failures} of {checks} FAILED");
return failures == 0 ? 0 : 1;

static string? Try(Action act)
{
    try { act(); return null; }
    catch (LogTableException ex) { return ex.Message; }
}

/// <summary>A log table in memory: rows numbered from 1, read the way the SQL reader reads.</summary>
sealed class FakeTable : ILogTableReader
{
    private readonly List<TableRow> _rows = [];

    public List<(string? After, int Max)> Calls { get; } = [];
    public bool Window { get; init; }
    public bool Overlap { get; set; }
    public Exception? Fail { get; set; }
    public bool Disposed { get; private set; }
    public string? Text { get; init; }

    public void Clear() => _rows.Clear();

    public void Add(long from, long to)
    {
        for (var n = from; n <= to; n++)
            _rows.Add(new TableRow(n.ToString(), Window ? null : n.ToString(), n,
                new DateTime(2026, 10, 23, 10, 0, 0).AddSeconds(n), n % 10 == 0 ? "Error" : "Information",
                Text ?? $"row {n}", null, null, null));
    }

    public Task<TableRead> ReadAsync(string? after, int max, CancellationToken ct)
    {
        Calls.Add((after, max));
        if (Fail is not null) throw Fail;

        if (Window || after is null)
            return Task.FromResult(new TableRead(_rows.TakeLast(max).ToList(), Window));

        var last = long.Parse(after);
        var from = Overlap ? last - 1 : last;
        return Task.FromResult(new TableRead(_rows.Where(r => r.Number > from).Take(max).ToList(), false));
    }

    public List<TablePageQuery> Pages { get; } = [];

    /// <summary>Waits for this before answering a page, to hold a read in flight.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<TablePage> PageAsync(TablePageQuery query, CancellationToken ct)
    {
        Pages.Add(query);
        if (Gate is { } gate) await gate.Task.WaitAsync(ct);
        if (Fail is not null) throw Fail;
        if (Disposed) throw new ObjectDisposedException(nameof(FakeTable));

        var rows = _rows.AsEnumerable().Reverse()
            .Where(r => query.Before is not string b || r.Number < long.Parse(b))
            .Where(r => query.Floor == LogSeverity.Unknown || LogParse.LevelOf(r.Level) >= query.Floor)
            .Where(r => LogTableRows.SearchTerm(query.Search) is not { } term || r.Message!.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(query.Size + 1).ToList();

        if (rows.Count <= query.Size) return new TablePage(rows, null);
        rows.RemoveAt(query.Size);
        return new TablePage(rows, rows[^1].Cursor);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

namespace MongoDB.Fake
{
    /// <summary>Stands in for a driver exception: what matters is the namespace.</summary>
    public sealed class FakeDriverException(string message) : Exception(message);
}
