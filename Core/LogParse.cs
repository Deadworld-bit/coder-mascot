using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoderMascot.Core;

/// <summary>
/// Turns the lines of a log file into entries.
///
/// An entry starts at a line that opens with a timestamp, with an ASP.NET console
/// level ("info: "), or that is a JSON object carrying a time or a Serilog
/// marker. Every other line belongs to the entry above it, which is how a stack
/// trace stays with its error. Lines before the first such header, and every
/// line of a file that has none, are entries of their own.
///
/// Log text is whatever the service was sent, so nothing in a line may throw out
/// of here or grow past the caps: a line that can't be read is text, and every
/// string an entry keeps is bounded.
/// </summary>
public static class LogParse
{
    public const int MaxDetailLines = 500;
    public const int MaxDetailChars = 32 * 1024;
    public const int MaxMessageChars = 1000;

    /// <summary>How far past the timestamp, and how many words in, a level still counts as the line's level.</summary>
    private const int LevelWindow = 64;
    private const int LevelWords = 6;

    /// <summary>Untimed entries held back until a timestamp says where they sort.</summary>
    private const int MaxWaiting = 2000;

    /// <summary>A file read from the middle starts at its first header — unless none turns up this soon.</summary>
    private const int MaxLeadIn = 1000;

    /// <summary>After this many lines that looked like JSON and weren't, a file's lines are not parsed as JSON any more.</summary>
    private const int MaxJsonFailures = 1000;

    private const int MaxValueChars = 1000;
    private const int MaxProperties = 50;

    // Every pattern is anchored with no nested repetition, so matching is linear;
    // the budget is a backstop.
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);

    private static readonly Regex DateTimeHead = new(
        @"^\[?(?:(?<y>[0-9]{4})(?<s1>[-/.])(?<mo>[0-9]{1,2})\k<s1>(?<d>[0-9]{1,2})" +
        @"|(?<a>[0-9]{1,2})(?<s2>[-/.])(?<b>[0-9]{1,2})\k<s2>(?<y2>[0-9]{4}))" +
        @"(?:T| {1,2})(?<h>[0-9]{1,2}):(?<mi>[0-9]{2})(?::(?<s>[0-9]{2}))?(?:[.,](?<f>[0-9]{1,9}))?" +
        @"(?: ?(?<ampm>[AaPp][Mm])(?![A-Za-z]))?" +
        @"(?: ?(?<z>Z(?![A-Za-z])|[+-][0-9]{2}:?[0-9]{2}(?![0-9])))?",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, Budget);

    private static readonly Regex TimeHead = new(
        @"^\[?(?<h>[0-9]{2}):(?<mi>[0-9]{2}):(?<s>[0-9]{2})(?:[.,](?<f>[0-9]{1,9}))?(?![0-9:])",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, Budget);

    // What `docker logs -t` puts in front of every line, continuation lines included.
    private static readonly Regex DockerStamp = new(
        @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?Z ",
        RegexOptions.CultureInvariant, Budget);

    // "Microsoft.Hosting.Lifetime[14]" alone on the header line: the ASP.NET
    // console puts the real message on the next, indented line.
    private static readonly Regex AspNetCategory = new(
        @"^[A-Za-z_][A-Za-z0-9_.`+<>,]*\[[0-9]+\]$",
        RegexOptions.CultureInvariant, Budget);

    private static readonly Dictionary<string, LogSeverity> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trace"] = LogSeverity.Trace, ["trc"] = LogSeverity.Trace, ["trce"] = LogSeverity.Trace,
        ["verbose"] = LogSeverity.Trace, ["vrb"] = LogSeverity.Trace,
        ["debug"] = LogSeverity.Debug, ["dbg"] = LogSeverity.Debug, ["dbug"] = LogSeverity.Debug,
        ["info"] = LogSeverity.Info, ["inf"] = LogSeverity.Info, ["information"] = LogSeverity.Info,
        ["warn"] = LogSeverity.Warning, ["wrn"] = LogSeverity.Warning, ["warning"] = LogSeverity.Warning,
        ["error"] = LogSeverity.Error, ["err"] = LogSeverity.Error, ["eror"] = LogSeverity.Error,
        ["fail"] = LogSeverity.Error,
        ["fatal"] = LogSeverity.Fatal, ["ftl"] = LogSeverity.Fatal, ["critical"] = LogSeverity.Fatal,
        ["crit"] = LogSeverity.Fatal, ["crt"] = LogSeverity.Fatal,
    };

    private static readonly string[] AspNetLevels = ["trce", "dbug", "info", "warn", "fail", "crit"];

    // CLEF entries are read by their reserved names only: a property the service
    // logged as {Message} or {Level} is data, not the entry's own message or level.
    private sealed record JsonKeys(string[] Time, string[] Level, string[] Message, string[] Template, string[] Error);

    private static readonly JsonKeys ClefKeys = new(["@t"], ["@l"], ["@m"], ["@mt"], ["@x"]);

    private static readonly JsonKeys PlainKeys = new(
        ["Timestamp", "@timestamp", "time", "ts", "date", "UtcTimeStamp"],
        ["Level", "severity", "lvl", "LogLevel", "log.level"],
        ["RenderedMessage", "Message", "msg"],
        ["MessageTemplate"],
        ["Exception", "error", "err", "stack", "stack_trace"]);

    private static readonly string[] HiddenKeys = ["@i", "@r", "Properties"];

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };

    /// <summary>
    /// The entries in <paramref name="lines"/>, in file order.
    /// </summary>
    /// <param name="fileDate">The date in the file's name, for lines that carry only a time of day.</param>
    /// <param name="fallback">Sort time for entries in a file that has no timestamps at all.</param>
    /// <param name="fromMiddle">The lines start part-way through the file, so leading continuation lines belong to an entry that wasn't read.</param>
    public static IEnumerable<LogEntry> Entries(
        IEnumerable<string> lines, string source, DateOnly? fileDate, DateTimeOffset fallback, bool fromMiddle = false)
    {
        var reader = new Reader(source, fileDate, fallback, fromMiddle);
        foreach (var line in lines)
        {
            reader.Feed(line);
            if (reader.Ready.Count == 0) continue;
            foreach (var entry in reader.Ready) yield return entry;
            reader.Ready.Clear();
        }

        reader.Finish();
        foreach (var entry in reader.Ready) yield return entry;
    }

    private readonly record struct Head(
        DateTimeOffset? Time, LogSeverity Level, string Message,
        string? Detail = null, bool Structured = false, bool Clef = false,
        bool Promote = false, bool Orphan = false, bool Cut = false);

    private sealed class Reader(string source, DateOnly? fileDate, DateTimeOffset fallback, bool fromMiddle)
    {
        public readonly List<LogEntry> Ready = [];

        private readonly List<Pending> _waiting = [];
        private Pending? _open;
        private DateTimeOffset? _last;
        private long _line;
        private bool _started = !fromMiddle;
        private bool _docker;
        private int _jsonFailures;

        public void Feed(string line)
        {
            _line++;

            var text = line;
            DateTimeOffset? stamped = null;
            Head? head;

            try
            {
                head = HeadOf(line);

                if (DockerTime(line) is { } docker)
                {
                    var rest = line[docker.Length..];

                    // Behind docker's own time, the service's line: a header there
                    // makes this a docker log, and from then on whatever isn't a
                    // header belongs to the entry above, as it would without -t.
                    if (HeadOf(rest) is { } inner)
                    {
                        _docker = true;
                        head = inner with { Time = inner.Time ?? docker.Time };
                    }
                    else if (_docker || (rest.Length > 0 && char.IsWhiteSpace(rest[0])))
                    {
                        head = null;
                        text = rest;
                        stamped = docker.Time;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException
                                           or OverflowException or RegexMatchTimeoutException)
            {
                head = null;
            }

            if (head is null)
            {
                if (!_started)
                {
                    if (_line <= MaxLeadIn) return;
                    _started = true;
                }

                if (TakesContinuation(_open))
                {
                    _open!.Append(text);
                    return;
                }

                if (string.IsNullOrWhiteSpace(text)) return;
                head = new Head(stamped, FindLevel(text, 0, 1).Level, text.Trim(), Orphan: true);
            }
            else if (head.Value is { Structured: true, Clef: false } && TakesContinuation(_open))
            {
                // A JSON body logged under a text entry, not an entry of its own.
                _open!.Append(text);
                return;
            }

            _started = true;
            Close();
            _open = new Pending(head.Value, line, _line);
        }

        // A JSON entry is whole on its line; an orphan is one line by definition.
        private static bool TakesContinuation(Pending? open) =>
            open is { Head.Orphan: false, Head.Structured: false };

        private Head? HeadOf(string line)
        {
            if (line.Length == 0 || line[0] != '{') return ReadHead(line, fileDate);

            if (_jsonFailures >= MaxJsonFailures || !line.AsSpan().TrimEnd().EndsWith("}")) return null;

            var head = ReadJson(line, out var broken);
            if (broken) _jsonFailures++;
            return head;
        }

        public void Finish()
        {
            Close();
            Flush(fallback);
        }

        private void Close()
        {
            if (_open is not { } entry) return;
            _open = null;

            if (entry.Head.Time is { } time)
            {
                // Untimed entries above the first timestamp sort with it, not at the file's date.
                Flush(time);
                _last = time;
                Ready.Add(entry.Build(source, time));
            }
            else if (_last is { } before)
            {
                Ready.Add(entry.Build(source, before));
            }
            else
            {
                _waiting.Add(entry);
                if (_waiting.Count >= MaxWaiting) Flush(fallback);
            }
        }

        private void Flush(DateTimeOffset at)
        {
            foreach (var entry in _waiting) Ready.Add(entry.Build(source, at));
            _waiting.Clear();
        }
    }

    private sealed class Pending(Head head, string firstLine, long line)
    {
        public Head Head { get; } = head;

        private StringBuilder? _detail;
        private string? _firstDetail;
        private int _lines;
        private int _dropped;

        public void Append(string text)
        {
            var used = _detail?.Length ?? 0;
            var room = MaxDetailChars - used - (_lines > 0 ? 1 : 0);
            if (_lines >= MaxDetailLines || room <= 0)
            {
                _dropped++;
                return;
            }

            _detail ??= new StringBuilder();
            if (_lines > 0) _detail.Append('\n');

            if (text.Length > room)
            {
                _detail.Append(text, 0, room);
                _dropped++;
            }
            else
            {
                _detail.Append(text);
            }

            if (_firstDetail is null && !string.IsNullOrWhiteSpace(text)) _firstDetail = text.Trim();
            _lines++;
        }

        public LogEntry Build(string source, DateTimeOffset sortTime)
        {
            var message = Head.Promote && _firstDetail is not null ? _firstDetail : Head.Message;
            if (message.Length > MaxMessageChars) message = message[..MaxMessageChars] + "…";

            var continuation = _detail?.ToString().TrimEnd();
            if (_dropped > 0)
                continuation += $"\n… {_dropped} more line{(_dropped == 1 ? "" : "s")} not shown";

            return new LogEntry
            {
                Time = Head.Time,
                SortTime = sortTime,
                Level = Head.Level,
                Message = Visible(message),
                Detail = Join(Head.Detail, string.IsNullOrEmpty(continuation) ? null : continuation),
                FirstLine = firstLine,
                Structured = Head.Structured,
                Cut = _dropped > 0 || Head.Cut,
                Source = source,
                Line = line,
            };
        }
    }

    /// <summary>U+FFFD, the replacement character: what a hidden character is shown as.</summary>
    private const char Mark = (char)0xFFFD;

    /// <summary>
    /// The one-line message with what would hide or reorder text in a list row made
    /// visible: control characters, line and paragraph separators, bidi overrides.
    /// The entry's text as written is kept for Copy.
    /// </summary>
    internal static string Visible(string message)
    {
        if (!message.Any(Hides)) return message;

        var chars = message.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] == '\t') chars[i] = ' ';
            else if (Hides(chars[i])) chars[i] = Mark;

        return new string(chars);
    }

    private static bool Hides(char c) =>
        char.IsControl(c) || c is (char)0x2028 or (char)0x2029 or (char)0x200E or (char)0x200F
            or (>= (char)0x202A and <= (char)0x202E) or (>= (char)0x2066 and <= (char)0x2069);

    private static (DateTimeOffset Time, int Length)? DockerTime(string line)
    {
        if (line.Length < 21 || !char.IsAsciiDigit(line[0])) return null;

        var m = DockerStamp.Match(line);
        if (!m.Success) return null;

        var time = DateTimeHead.Match(line);
        return time.Success && ToTime(time) is { } t ? (t, m.Length) : null;
    }

    private static Head? ReadHead(string line, DateOnly? fileDate)
    {
        if (line.Length == 0) return null;

        var first = line[0];
        if (char.IsAsciiDigit(first) || first == '[')
        {
            var m = DateTimeHead.Match(line);
            if (m.Success && ToTime(m) is { } time) return TextHead(line, m.Length, time);

            m = TimeHead.Match(line);
            if (m.Success)
            {
                // A bare time of day is only a header with a level right behind it;
                // "10:15:30" alone is as likely to be part of a message.
                var found = FindLevel(line, m.Length, 1);
                if (found.Level != LogSeverity.Unknown)
                {
                    return fileDate is { } day && ToTime(m, day) is { } dated
                        ? TextHead(line, m.Length, dated)
                        : new Head(null, found.Level, line.Trim());
                }
            }
        }

        if (line.Length >= 5 && line[4] == ':' && (line.Length == 5 || line[5] == ' '))
        {
            var word = line[..4];
            if (Array.IndexOf(AspNetLevels, word) >= 0)
                return new Head(null, Words[word], line[5..].Trim(), Promote: true);
        }

        return null;
    }

    private static Head TextHead(string line, int end, DateTimeOffset time)
    {
        var (level, start, stop) = FindLevel(line, end, LevelWords);
        var message = WithoutLevel(line, end, start, stop);
        var promote = message.Length == 0 || AspNetCategory.IsMatch(message);

        return new Head(time, level, message, Promote: promote);
    }

    /// <summary>
    /// The level among the first few words from <paramref name="from"/>, with where it
    /// sits so it can be cut out of the message. The first word counts as it is;
    /// a later one only when it is written as a level — bracketed or in capitals —
    /// so "Call API error: timeout" is not an error with "error" cut out of it.
    /// </summary>
    private static (LogSeverity Level, int Start, int End) FindLevel(string text, int from, int maxWords)
    {
        var limit = Math.Min(text.Length, from + LevelWindow);
        var i = from;

        for (var word = 0; word < maxWords; word++)
        {
            while (i < limit && IsDelimiter(text[i])) i++;
            if (i >= limit) break;

            var start = i;
            while (i < text.Length && !IsDelimiter(text[i]) && i - start <= 11) i++;

            if (i - start > 11)
            {
                // Longer than any level name; step over the rest of it.
                while (i < limit && !IsDelimiter(text[i])) i++;
                continue;
            }

            if (Words.TryGetValue(text.Substring(start, i - start), out var level)
                && (word == 0 || Bracketed(text, start, i) || Capitals(text, start, i)))
                return (level, start, i);
        }

        return (LogSeverity.Unknown, -1, -1);
    }

    private static bool Bracketed(string text, int start, int end) =>
        start > 0 && end < text.Length && Closer(text[start - 1]) is { } close && text[end] == close;

    private static bool Capitals(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!char.IsAsciiLetterUpper(text[i])) return false;
        return true;
    }

    private static bool IsDelimiter(char c) =>
        char.IsWhiteSpace(c) || c is '[' or ']' or '(' or ')' or '<' or '>' or '|' or ':' or ','
            or ';' or '-' or '=' or '{' or '}' or '"' or '\'';

    private static string WithoutLevel(string line, int from, int start, int end)
    {
        if (start < 0) return line.AsSpan(from).TrimStart(" \t]|:-").TrimEnd().ToString();

        // Take the brackets hugging the level with it: [INF] (ERROR) <INFO> |INFO|
        if (Bracketed(line, start, end) && start > from)
        {
            start--;
            end++;
        }

        var before = line.AsSpan(from, start - from).Trim(" \t|:-");
        var after = line.AsSpan(end).TrimStart(" \t]|:-").TrimEnd();

        return before.IsEmpty ? after.ToString() : $"{before} {after}".TrimEnd();
    }

    private static char? Closer(char open) => open switch
    {
        '[' => ']',
        '(' => ')',
        '<' => '>',
        '|' => '|',
        _ => null,
    };

    private static DateTimeOffset? ToTime(Match m, bool utc = false)
    {
        int year, month, day;
        if (m.Groups["y"].Success)
        {
            year = Number(m, "y");
            month = Number(m, "mo");
            day = Number(m, "d");
        }
        else
        {
            var a = Number(m, "a");
            var b = Number(m, "b");
            year = Number(m, "y2");

            // Day first, as written in Vietnam, unless the numbers rule it out.
            (day, month) = a > 12 || b <= 12 ? (a, b) : (b, a);
        }

        if (year is < 1990 or > 2100 || month is < 1 or > 12) return null;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return null;

        return Clock(m, new DateOnly(year, month, day), utc);
    }

    private static DateTimeOffset? ToTime(Match m, DateOnly day) => Clock(m, day, utc: false);

    private static DateTimeOffset? Clock(Match m, DateOnly day, bool utc)
    {
        var hour = Number(m, "h");
        var minute = Number(m, "mi");
        var second = m.Groups["s"].Success ? Number(m, "s") : 0;

        if (m.Groups["ampm"].Success)
        {
            if (hour is < 1 or > 12) return null;
            var pm = m.Groups["ampm"].Value[0] is 'P' or 'p';
            hour = hour % 12 + (pm ? 12 : 0);
        }

        if (hour > 23 || minute > 59 || second > 60) return null;
        second = Math.Min(second, 59);

        var ticks = 0L;
        if (m.Groups["f"].Success)
        {
            var digits = m.Groups["f"].Value;
            digits = digits.Length > 7 ? digits[..7] : digits.PadRight(7, '0');
            ticks = long.Parse(digits, CultureInfo.InvariantCulture);
        }

        var clock = day.ToDateTime(new TimeOnly(hour, minute, second)).AddTicks(ticks);

        if (m.Groups["z"].Success && Offset(m.Groups["z"].Value) is { } offset)
            return new DateTimeOffset(clock, offset);

        // No zone written: the service's clock, which for these servers is the PC's zone too.
        return utc ? new DateTimeOffset(clock, TimeSpan.Zero) : new DateTimeOffset(clock, TimeZoneInfo.Local.GetUtcOffset(clock));
    }

    /// <summary>The zone, or null for one DateTimeOffset would refuse (beyond ±14:00).</summary>
    private static TimeSpan? Offset(string z)
    {
        if (z == "Z") return TimeSpan.Zero;

        var digits = z.Replace(":", "");
        var hours = int.Parse(digits.AsSpan(1, 2), CultureInfo.InvariantCulture);
        var minutes = int.Parse(digits.AsSpan(3, 2), CultureInfo.InvariantCulture);

        var span = new TimeSpan(hours, minutes, 0);
        if (minutes > 59 || span > TimeSpan.FromHours(14)) return null;

        return z[0] == '-' ? -span : span;
    }

    private static int Number(Match m, string group) =>
        int.Parse(m.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <param name="broken">The line looked like JSON and wasn't, or couldn't be read as text.</param>
    private static Head? ReadJson(string line, out bool broken)
    {
        broken = false;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // ArgumentException: a lone surrogate, which a line cut mid-emoji leaves behind.
            broken = true;
            return null;
        }

        using (doc)
        {
            try
            {
                return JsonHead(doc.RootElement);
            }
            catch (InvalidOperationException)
            {
                // An escaped lone surrogate (\uD800) parses but can't be read as a string.
                broken = true;
                return null;
            }
        }
    }

    private static Head? JsonHead(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var top = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject()) top.TryAdd(property.Name, property.Value);

        var clef = top.ContainsKey("@t") || top.ContainsKey("@mt") || top.ContainsKey("@m");
        var keys = clef ? ClefKeys : PlainKeys;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var time = Pick(top, keys.Time, used) is { } t ? JsonTime(t) : null;

        // A JSON body dumped under a log line is not an entry of its own.
        if (time is null && !clef) return null;

        // CLEF leaves @l out for Information.
        var level = Pick(top, keys.Level, used) is { } l
            ? JsonLevel(l)
            : clef ? LogSeverity.Info : LogSeverity.Unknown;

        // Serilog's JsonFormatter nests the properties; CLEF keeps them at the top.
        var values = new Dictionary<string, JsonElement>(top, StringComparer.OrdinalIgnoreCase);
        var nested = top.TryGetValue("Properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : (JsonElement?)null;
        if (nested is { } inner)
            foreach (var property in inner.EnumerateObject())
                values.TryAdd(property.Name, property.Value);

        const int room = MaxMessageChars + MaxDetailChars;
        var message = Pick(top, keys.Message, used) is { } m ? Display(m, room) : null;
        if (message is null && Pick(top, keys.Template, used) is { } template && template.ValueKind == JsonValueKind.String)
        {
            var renderings = top.TryGetValue("@r", out var r) && r.ValueKind == JsonValueKind.Array ? r : (JsonElement?)null;
            message = Render(template.GetString()!, values, renderings);
        }

        message ??= "";
        var exception = Pick(top, keys.Error, used) is { } x ? Display(x, MaxDetailChars) : null;

        string? rest = null;
        var newline = message.IndexOf('\n');
        if (newline >= 0)
        {
            rest = message[(newline + 1)..].TrimEnd();
            message = message[..newline].TrimEnd('\r');
        }

        var shown = top
            .Where(kv => !used.Contains(kv.Key) && !HiddenKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .Concat(nested is { } n ? n.EnumerateObject().Select(e => KeyValuePair.Create(e.Name, e.Value)) : [])
            .ToList();

        string? properties = null;
        if (shown.Count > 0)
        {
            var block = new StringBuilder();
            foreach (var (key, value) in shown.Take(MaxProperties))
            {
                if (block.Length > 0) block.Append('\n');
                block.Append(key).Append(": ").Append(Display(value, MaxValueChars).ReplaceLineEndings(" "));
            }

            if (shown.Count > MaxProperties) block.Append($"\n… {shown.Count - MaxProperties} more");
            properties = block.ToString();
        }

        var detail = Join(Join(rest, exception), properties, "\n\n");
        var cut = detail is { Length: > MaxDetailChars };
        if (cut) detail = detail![..MaxDetailChars] + "\n… cut at the size limit";

        return new Head(time, level, message.Trim(), detail, Structured: true, Clef: clef, Cut: cut);
    }

    private static JsonElement? Pick(Dictionary<string, JsonElement> top, string[] keys, HashSet<string> used)
    {
        foreach (var key in keys)
        {
            if (!top.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            used.Add(key);
            return value;
        }

        return null;
    }

    private static DateTimeOffset? JsonTime(JsonElement value)
    {
        DateTimeOffset? time = null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!.Trim();
            var m = DateTimeHead.Match(text);
            if (m.Success) time = ToTime(m);

            if (time is null &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                time = parsed;
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
        {
            // Epoch milliseconds (pino, JavaScript) or seconds.
            var ms = number > 1e11 ? number : number * 1000;
            if (ms is > 0 and < 4.2e12) time = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        }

        return time is { Year: >= 1990 and <= 2100 } ? time : null;
    }

    private static LogSeverity JsonLevel(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return Words.TryGetValue(value.GetString()!.Trim(), out var level) ? level : LogSeverity.Unknown;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? NumericLevel(n) : LogSeverity.Unknown;
    }

    /// <summary>
    /// Microsoft.Extensions.Logging's and NLog's 0–5, pino/bunyan's 10–60, or
    /// log4net's 10000–120000 (INFO 40000, ERROR 70000) as stored by its AdoNetAppender.
    /// </summary>
    private static LogSeverity NumericLevel(long n) => n switch
    {
        < 0 => LogSeverity.Unknown,
        0 => LogSeverity.Trace,
        1 => LogSeverity.Debug,
        2 => LogSeverity.Info,
        3 => LogSeverity.Warning,
        4 => LogSeverity.Error,
        5 => LogSeverity.Fatal,
        <= 10 => LogSeverity.Trace,
        <= 20 => LogSeverity.Debug,
        <= 30 => LogSeverity.Info,
        <= 40 => LogSeverity.Warning,
        <= 50 => LogSeverity.Error,
        < 1000 => LogSeverity.Fatal,
        < 30000 => LogSeverity.Trace,
        < 40000 => LogSeverity.Debug,
        < 60000 => LogSeverity.Info,
        < 70000 => LogSeverity.Warning,
        < 90000 => LogSeverity.Error,
        _ => LogSeverity.Fatal,
    };

    // Where NumericLevel's answer can change: from each of these up to the next it is the same.
    private static readonly long[] LevelSteps = [0, 1, 2, 3, 4, 5, 6, 11, 21, 31, 41, 51, 1000, 30000, 40000, 60000, 70000, 90000];

    // What the usual scales store, for a column that keeps its number as text.
    private static readonly long[] UsualLevelNumbers =
        [0, 1, 2, 3, 4, 5, 10, 20, 30, 40, 50, 60, 10000, 20000, 30000, 40000, 50000, 60000, 70000, 80000, 90000, 100000, 110000, 120000];

    /// <summary>
    /// The text a level column may hold for <paramref name="floor"/> or worse, for a filter the
    /// server runs: every name in the casings services write, and the usual numbers as text.
    /// </summary>
    internal static IReadOnlyList<string> LevelNamesFrom(LogSeverity floor)
    {
        var names = new List<string>();
        foreach (var (word, level) in Words)
        {
            if (level < floor) continue;
            names.Add(word.ToLowerInvariant());
            names.Add(word.ToUpperInvariant());
            names.Add(char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant());
        }

        names.AddRange(UsualLevelNumbers.Where(n => NumericLevel(n) >= floor).Select(n => n.ToString(CultureInfo.InvariantCulture)));
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The numbers that read as <paramref name="floor"/> or worse, as inclusive ranges; a null top is "and up".</summary>
    internal static IReadOnlyList<(long Low, long? High)> LevelNumbersFrom(LogSeverity floor)
    {
        var ranges = new List<(long Low, long? High)>();
        for (var i = 0; i < LevelSteps.Length; i++)
        {
            if (NumericLevel(LevelSteps[i]) < floor) continue;

            long? high = i + 1 < LevelSteps.Length ? LevelSteps[i + 1] - 1 : null;
            if (ranges.Count > 0 && ranges[^1].High == LevelSteps[i] - 1) ranges[^1] = (ranges[^1].Low, high);
            else ranges.Add((LevelSteps[i], high));
        }

        return ranges;
    }

    /// <summary>A level read from a database column: a name, or one of the numeric scales.</summary>
    internal static LogSeverity LevelOf(object? value)
    {
        switch (value)
        {
            case null:
                return LogSeverity.Unknown;
            case string text:
                text = text.Trim();
                if (Words.TryGetValue(text, out var level)) return level;
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? NumericLevel(parsed)
                    : LogSeverity.Unknown;
            case byte or sbyte or short or ushort or int or uint or long:
                return NumericLevel(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case decimal or double or float:
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsFinite(d) && Math.Abs(d) < 1e12 ? NumericLevel((long)d) : LogSeverity.Unknown;
            default:
                return LogSeverity.Unknown;
        }
    }

    /// <summary>
    /// A time read from a database column. A clock time with no zone is the PC's
    /// zone, as in the files — or UTC when the source says its column holds UTC.
    /// </summary>
    internal static DateTimeOffset? TimeOf(object? value, bool utc)
    {
        DateTimeOffset? time = value switch
        {
            DateTimeOffset offset => offset,

            // Before any offset is applied: 0001-01-01 (default(DateTime) written to datetime2)
            // shifted by +07:00 is out of DateTimeOffset's range and throws.
            DateTime { Year: < 1990 or > 2100 } => null,
            DateTime { Kind: DateTimeKind.Utc } at => new DateTimeOffset(at, TimeSpan.Zero),
            DateTime { Kind: DateTimeKind.Local } at => new DateTimeOffset(at),
            DateTime at => utc ? new DateTimeOffset(at, TimeSpan.Zero) : new DateTimeOffset(at, TimeZoneInfo.Local.GetUtcOffset(at)),
            string text => TextTime(text.Trim(), utc),
            long or int => Epoch(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            double number => Epoch(number),
            _ => null,
        };

        return time is { Year: >= 1990 and <= 2100 } ? time : null;
    }

    private static DateTimeOffset? TextTime(string text, bool utc)
    {
        try
        {
            var m = DateTimeHead.Match(text);
            if (m.Success && ToTime(m, utc) is { } time) return time;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        var style = utc ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, style, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? Epoch(double number)
    {
        if (!double.IsFinite(number)) return null;

        // Epoch milliseconds (pino, JavaScript) or seconds.
        var ms = number > 1e11 ? number : number * 1000;
        return ms is > 0 and < 4.2e12 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;
    }

    /// <summary>
    /// A Serilog message template with its property values filled in. Tokens with a
    /// format take CLEF's pre-rendered <c>@r</c> strings, in order, when present.
    ///
    /// Bounded and linear: services here interpolate request text into templates,
    /// so a template can be a wall of tokens built to multiply one line into megabytes.
    /// </summary>
    internal static string Render(string template, IReadOnlyDictionary<string, JsonElement> values, JsonElement? renderings)
    {
        const int room = MaxMessageChars + MaxDetailChars;

        var text = new StringBuilder(Math.Min(template.Length, room) + 32);
        var rendered = renderings is { } r ? r.EnumerateArray().ToArray() : [];
        var next = 0;
        var close = -1;
        var i = 0;

        while (i < template.Length && text.Length < room)
        {
            var c = template[i];

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                text.Append('}');
                i += 2;
                continue;
            }

            if (c != '{')
            {
                text.Append(c);
                i++;
                continue;
            }

            if (i + 1 < template.Length && template[i + 1] == '{')
            {
                text.Append('{');
                i += 2;
                continue;
            }

            // The first '}' after this '{' is the same one for every '{' before it.
            if (close <= i) close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                text.Append(template, i, Math.Min(template.Length - i, room));
                break;
            }

            var reopen = template.IndexOf('{', i + 1, close - i - 1);
            if (reopen >= 0)
            {
                text.Append(template, i, reopen - i);
                i = reopen;
                continue;
            }

            var token = template.AsSpan(i + 1, close - i - 1);
            var formatted = token.IndexOf(':') >= 0;
            var name = token.TrimStart("@$");
            var cut = name.IndexOfAny(':', ',');
            if (cut >= 0) name = name[..cut];

            if (formatted && next < rendered.Length)
                text.Append(Display(rendered[next++], MaxValueChars));
            else if (values.TryGetValue(name.ToString(), out var value))
                text.Append(Display(value, MaxValueChars));
            else
                text.Append('{').Append(token).Append('}');

            i = close + 1;
        }

        if (text.Length > room || (text.Length == room && i < template.Length))
        {
            text.Length = room;
            text.Append('…');
        }

        return text.ToString();
    }

    private static string Display(JsonElement value, int max)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null => "null",
            _ => value.GetRawText(),
        };

        return text.Length > max ? text[..max] + "…" : text;
    }

    private static string? Join(string? first, string? second, string between = "\n")
    {
        if (string.IsNullOrEmpty(first)) return string.IsNullOrEmpty(second) ? null : second;
        return string.IsNullOrEmpty(second) ? first : first + between + second;
    }
}
