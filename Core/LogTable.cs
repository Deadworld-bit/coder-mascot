using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CoderMascot.Core;

/// <summary>One row as a reader hands it over: raw values for the mapped roles, already cut to size by the query.</summary>
/// <param name="Key">Unique within the table, to tell a row already held from a new one.</param>
/// <param name="Cursor">Where the next read starts after this row (the id), or null in window mode.</param>
public sealed record TableRow(
    string Key, string? Cursor, long? Number,
    object? Time, object? Level, string? Message, string? Exception, string? Properties, string? Logger);

/// <param name="Window">The rows are the newest ones, to replace what is held, not to add to it.</param>
public sealed record TableRead(IReadOnlyList<TableRow> Rows, bool Window);

/// <param name="Scale">Digits after the point, for numeric/decimal: a decimal(18,2) id doesn't count up in whole steps.</param>
public sealed record TableColumn(string Name, string Type, int? Scale = null);

/// <summary>One page of a table for the Logs page, newest first, with the level and the search applied by the server.</summary>
/// <param name="Before">Where the page starts: the <see cref="TablePage.Older"/> of the page above it, or null for the newest.</param>
/// <param name="Floor">The least level shown; <see cref="LogSeverity.Unknown"/> for every row.</param>
public sealed record TablePageQuery(int Size, object? Before = null, LogSeverity Floor = LogSeverity.Unknown, string? Search = null);

/// <param name="Rows">Newest first.</param>
/// <param name="Older">Where the page below starts, or null when there is none. Only the reader that made it reads it.</param>
/// <param name="TiesCut">Read by time: more rows share the page's oldest time than were taken onto it.</param>
public sealed record TablePage(IReadOnlyList<TableRow> Rows, object? Older, bool TiesCut = false);

/// <summary>A page as the Logs page shows it: entries newest first, or why there are none.</summary>
/// <param name="Slow">The server took too long to answer.</param>
public sealed record TablePageView(IReadOnlyList<LogEntry> Entries, object? Older, bool TiesCut, string? Problem, bool Slow = false)
{
    public static TablePageView Failed(string problem, bool slow = false) => new([], null, false, problem, slow);
}

/// <summary>What a draft source's connection reaches: for the form, before anything is saved.</summary>
public sealed record TableProbe(
    string? Problem, string? Host, IReadOnlyList<string> Tables, IReadOnlyList<TableColumn> Columns,
    IReadOnlyList<LogEntry> Preview);

/// <summary>A reader's failure, already in words that are safe to show: no connection string, no password.</summary>
/// <param name="slow">The server took too long: a reason to wait before asking again, not to open a new reader.</param>
public sealed class LogTableException(string message, bool slow = false) : Exception(message)
{
    public bool Slow { get; } = slow;
}

/// <summary>Reads one log table. Rows come oldest first.</summary>
public interface ILogTableReader : IAsyncDisposable
{
    /// <summary>
    /// The newest <paramref name="max"/> rows when <paramref name="after"/> is null (and
    /// always in window mode); otherwise the rows after that cursor, up to <paramref name="max"/>.
    /// Throws <see cref="LogTableException"/> with a sentence fit for the page.
    /// </summary>
    Task<TableRead> ReadAsync(string? after, int max, CancellationToken ct);

    /// <summary>
    /// One page, newest first, read when someone asks for it and never on the poll: a level or a
    /// search can't use an index, so the server reads back from the newest row until the page is full.
    /// Throws <see cref="LogTableException"/> like <see cref="ReadAsync"/>.
    /// </summary>
    Task<TablePage> PageAsync(TablePageQuery query, CancellationToken ct);
}

/// <summary>Turning table rows into entries, and guessing which column is which.</summary>
public static class LogTableRows
{
    /// <summary>What a query cuts each long column to, before anything crosses the network.</summary>
    public const int MessageChars = 8000;
    public const int ExceptionChars = 16000;
    public const int PropertiesChars = 8000;
    public const int LoggerChars = 400;

    /// <summary>The most rows one page asks for.</summary>
    public const int MaxPageRows = 200;

    /// <summary>
    /// Read by time, the rows that share a page's oldest time are taken onto it whole, so the next
    /// page can start strictly below that time; this many at most.
    /// </summary>
    public const int MaxTies = 500;

    public const int MaxSearchChars = 200;

    public const string SlowPage =
        "The server took too long, so the query was stopped. A level or a search reads back from the newest row " +
        "until the page is full, and on a large table a rare match takes that long.";

    private const int MaxProperties = 50;
    private const int MaxValueChars = 1000;

    public static LogEntry ToEntry(TableRow row, string source, bool utc)
    {
        var time = LogParse.TimeOf(row.Time, utc);
        var level = LogParse.LevelOf(row.Level);

        var message = (row.Message ?? "").Replace("\r\n", "\n");
        string? rest = null;
        var newline = message.IndexOf('\n');
        if (newline >= 0)
        {
            rest = message[(newline + 1)..].TrimEnd();
            message = message[..newline];
        }

        message = message.Trim();
        if (message.Length > LogParse.MaxMessageChars) message = message[..LogParse.MaxMessageChars] + "…";
        message = LogParse.Visible(message);

        var extra = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(row.Logger)) extra.Append("Logger: ").Append(row.Logger.Trim());
        if (Properties(row.Properties) is { } properties)
        {
            if (extra.Length > 0) extra.Append('\n');
            extra.Append(properties);
        }

        var detail = Join(Join(rest, row.Exception?.TrimEnd(), "\n"), extra.Length > 0 ? extra.ToString() : null, "\n\n");
        var cut = detail is { Length: > LogParse.MaxDetailChars };
        if (cut) detail = detail![..LogParse.MaxDetailChars] + "\n… cut at the size limit";

        var when = time is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) : "-";

        return new LogEntry
        {
            Time = time,
            SortTime = time ?? DateTimeOffset.MinValue,
            Level = level,
            Message = message,
            Detail = detail,
            FirstLine = $"{when} [{level}] {message}",
            Cut = cut,
            Source = source,
            Line = row.Number ?? 0,
        };
    }

    /// <summary>
    /// Serilog's XML properties column (<c>&lt;properties&gt;&lt;property key='…'&gt;</c>),
    /// a JSON object (a <c>LogEvent</c> column, a MongoDB sub-document), or the text as it is.
    /// </summary>
    internal static string? Properties(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        var pairs = text[0] switch
        {
            '<' => XmlPairs(text),
            '{' => JsonPairs(text),
            _ => null,
        };

        if (pairs is null) return Cap(text, MaxValueChars * 4);

        var block = new StringBuilder();
        var count = 0;
        foreach (var (key, value) in pairs)
        {
            if (count++ == MaxProperties)
            {
                block.Append("\n…");
                break;
            }

            if (block.Length > 0) block.Append('\n');
            block.Append(key).Append(": ").Append(Cap(value, MaxValueChars).ReplaceLineEndings(" "));
        }

        return block.Length == 0 ? null : block.ToString();
    }

    private static List<(string, string)>? XmlPairs(string text)
    {
        try
        {
            // No DTDs, no external anything: the column is whatever the service wrote.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = PropertiesChars * 4L,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };

            using var reader = XmlReader.Create(new StringReader(text), settings);
            var root = XDocument.Load(reader).Root;
            if (root is null) return null;

            return root.Elements()
                .Select(e => ((string?)e.Attribute("key") ?? e.Name.LocalName, Flatten(e)))
                .ToList();
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // A structure or sequence reads as its leaves, key=value where they have keys.
    private static string Flatten(XElement element) =>
        element.HasElements
            ? string.Join(", ", element.Descendants()
                .Where(d => !d.HasElements)
                .Select(d => (string?)d.Attribute("key") is { } k ? $"{k}={d.Value}" : d.Value))
            : element.Value;

    private static List<(string, string)>? JsonPairs(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // A Serilog LogEvent column carries the event; its properties are the interesting part.
            if (root.TryGetProperty("Properties", out var inner) && inner.ValueKind == JsonValueKind.Object) root = inner;

            return root.EnumerateObject()
                .Select(p => (p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText()))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static readonly string[] IdNames = ["Id", "LogId", "Log_Id", "_id"];

    private static readonly string[] TimeNames =
        ["TimeStamp", "UtcTimeStamp", "Logged", "LogDate", "LogTime", "Date", "DateTime", "Time",
         "CreatedDate", "CreateDate", "CreatedAt", "CreatedOn", "CreateTime", "CreatedTime", "NgayTao", "ThoiGian"];

    private static readonly string[] LevelNames = ["Level", "LogLevel", "Severity", "LevelName", "LogType", "Type", "MucDo"];

    private static readonly string[] MessageNames =
        ["RenderedMessage", "Message", "Msg", "LogMessage", "Content", "Description", "NoiDung", "MessageTemplate", "Action"];

    private static readonly string[] ExceptionNames = ["Exception", "Exceptions", "ExceptionMessage", "Error", "ErrorMessage", "StackTrace"];
    private static readonly string[] PropertiesNames = ["Properties", "LogEvent", "Data", "Extra", "Context", "AdditionalData", "Changes"];
    private static readonly string[] LoggerNames = ["Logger", "SourceContext", "Category", "Source", "Callsite", "Class"];

    private static readonly string[] IntegerTypes = ["int", "bigint", "smallint", "tinyint", "numeric", "decimal", "objectId", "int32", "int64"];
    private static readonly string[] TimeTypes = ["datetime", "datetime2", "datetimeoffset", "smalldatetime", "date", "string", "varchar", "nvarchar"];

    /// <summary>
    /// Which column is which, from the names Serilog, NLog, log4net and the usual
    /// hand-made tables give them — in any case and spelt with or without underscores,
    /// so <c>created_date</c> is a CreatedDate. An audit table's <c>action</c> and
    /// <c>changes</c> come last. A guess the form shows and lets the user change.
    /// </summary>
    public static LogColumnMap Guess(IReadOnlyList<TableColumn> columns)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? Pick(string[] names, Func<TableColumn, bool>? fits = null)
        {
            foreach (var name in names)
            {
                // The name as written before the same name without its underscores.
                var column = columns
                    .Where(c => string.Equals(Bare(c.Name), Bare(name), StringComparison.OrdinalIgnoreCase)
                                && !taken.Contains(c.Name) && (fits is null || fits(c)))
                    .OrderBy(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .FirstOrDefault();

                if (column is null) continue;
                taken.Add(column.Name);
                return column.Name;
            }

            return null;
        }

        // Order matters: the specific names claim their column before the general ones can.
        var id = Pick(IdNames, IsInteger);
        var time = Pick(TimeNames, c => TimeTypes.Contains(c.Type, StringComparer.OrdinalIgnoreCase));
        var message = Pick(MessageNames);
        var exception = Pick(ExceptionNames);
        var properties = Pick(PropertiesNames);
        var logger = Pick(LoggerNames);
        var level = Pick(LevelNames);

        return new LogColumnMap
        {
            Id = id, Time = time, Level = level, Message = message,
            Exception = exception, Properties = properties, Logger = logger,
        };
    }

    private static string Bare(string name) => name.Replace("_", "").Replace("-", "").Replace(" ", "");

    /// <summary>A column whose values count up in whole steps: an integer type, or numeric/decimal with no scale.</summary>
    public static bool IsInteger(TableColumn column) =>
        IntegerTypes.Contains(column.Type, StringComparer.OrdinalIgnoreCase) && column.Scale is null or 0;

    /// <summary>Text, as far as ordering goes: a string time sorts by its characters, which is not by time.</summary>
    public static bool IsText(string type) =>
        type.Contains("char", StringComparison.OrdinalIgnoreCase) || type.Contains("text", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "string", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a search box holds, as the reader searches for it: trimmed and cut to size, or null for no search.</summary>
    public static string? SearchTerm(string? raw)
    {
        // A control character can't go into a BSON pattern, and is nothing a search box means.
        var term = raw is null ? null : new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (string.IsNullOrEmpty(term)) return null;
        if (term.Length <= MaxSearchChars) return term;

        // Not between the halves of a surrogate pair.
        var end = char.IsHighSurrogate(term[MaxSearchChars - 1]) ? MaxSearchChars - 1 : MaxSearchChars;
        return term[..end];
    }

    private static string Cap(string text, int max) => text.Length > max ? text[..max] + "…" : text;

    private static string? Join(string? first, string? second, string between)
    {
        if (string.IsNullOrEmpty(first)) return string.IsNullOrEmpty(second) ? null : second;
        return string.IsNullOrEmpty(second) ? first : first + between + second;
    }
}
