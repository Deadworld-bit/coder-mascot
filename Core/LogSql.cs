using System.Globalization;
using System.Text;

namespace CoderMascot.Core;

/// <summary>
/// The resolved shape of a SQL Server log table: names exactly as the catalog spells
/// them, and what the catalog says about the id and time columns.
/// </summary>
/// <param name="IdWhole">The id is a whole number: an integer type, or numeric/decimal with no scale.</param>
/// <param name="IdUsable">The id is an identity or a one-column unique index, so "after the last id" is a seek.</param>
/// <param name="TimeIndexed">The time column leads an index, so "the newest by time" is a seek too.</param>
/// <param name="IsView">A view: its indexes can't be seen, so the DBA who granted it is trusted to have made it cheap.</param>
/// <param name="LevelType">The level column's type: a number is filtered by range, text by name.</param>
public sealed record SqlShape(
    string Schema, string Table, LogColumnMap Columns,
    string? IdType, bool IdWhole, string? TimeType, bool IdUsable, bool TimeIndexed, bool IsView, string? LevelType = null)
{
    /// <summary>Read after the last id; otherwise the newest by time.</summary>
    public bool ById => Columns.Id is not null && IdWhole && (IdUsable || IsView);
}

/// <summary>
/// The T-SQL the reader sends, built where it can be tested without a server.
///
/// Every column name is one the catalog returned for this table; the schema and table
/// were matched against the catalog with parameters. All of them are bracket-quoted
/// with any ']' doubled, and every value is a parameter. Nothing newer than SQL Server
/// 2008 is used, because some of the servers it will meet are older than 2016.
/// </summary>
public static class LogSql
{
    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    /// <summary>"dbo.Logs", or "dbo.[Log.Archive]" for a name the plain form would split wrongly.</summary>
    public static string Display(string schema, string table) => $"{Part(schema)}.{Part(table)}";

    private static string Part(string name) => name.IndexOfAny(['.', '[', ']']) >= 0 || name.Trim() != name ? Quote(name) : name;

    /// <summary>"dbo.Logs", "[dbo].[Logs]", "Logs" (dbo) — or null for anything else.</summary>
    public static (string Schema, string Table)? SplitTable(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        var parts = new List<string>();
        var part = new StringBuilder();
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                var close = i + 1;
                var name = new StringBuilder();
                for (; close < text.Length; close++)
                {
                    if (text[close] != ']') { name.Append(text[close]); continue; }
                    if (close + 1 < text.Length && text[close + 1] == ']') { name.Append(']'); close++; continue; }
                    break;
                }

                if (close >= text.Length) return null;
                part.Append(name);
                i = close + 1;
            }
            else if (text[i] == '.')
            {
                parts.Add(part.ToString());
                part.Clear();
                i++;
            }
            else
            {
                part.Append(text[i]);
                i++;
            }
        }

        parts.Add(part.ToString());
        if (parts.Any(p => p.Trim().Length == 0)) return null;

        return parts.Count switch
        {
            1 => ("dbo", parts[0].Trim()),
            2 => (parts[0].Trim(), parts[1].Trim()),
            _ => null,
        };
    }

    public const string TablesQuery =
        "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
        "WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW') ORDER BY TABLE_SCHEMA, TABLE_NAME";

    public const string ColumnsQuery =
        "SELECT COLUMN_NAME, DATA_TYPE, NUMERIC_SCALE FROM INFORMATION_SCHEMA.COLUMNS " +
        "WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION";

    /// <summary>
    /// Is it a view; is the id an identity or a one-column unique index; does the time
    /// column lead an index. Parameters: @schema, @table, @id, @time (null when unmapped).
    /// </summary>
    public const string IndexQuery = """
        DECLARE @o int = OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@table));
        SELECT
          CAST(ISNULL(OBJECTPROPERTY(@o, 'IsView'), 0) AS int),
          CASE WHEN @id IS NULL THEN 0
               WHEN COLUMNPROPERTY(@o, @id, 'IsIdentity') = 1 THEN 1
               WHEN EXISTS (
                 SELECT 1 FROM sys.indexes i
                 JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                 JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                 WHERE i.object_id = @o AND i.is_unique = 1 AND ic.key_ordinal = 1 AND c.name = @id
                   AND (SELECT COUNT(*) FROM sys.index_columns k
                        WHERE k.object_id = i.object_id AND k.index_id = i.index_id AND k.key_ordinal > 0) = 1)
               THEN 1 ELSE 0 END,
          CASE WHEN @time IS NULL THEN 0
               WHEN EXISTS (
                 SELECT 1 FROM sys.index_columns ic
                 JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                 WHERE ic.object_id = @o AND ic.key_ordinal = 1 AND c.name = @time)
               THEN 1 ELSE 0 END
        """;

    /// <summary>
    /// The read. By id: the newest rows (<paramref name="after"/> false) or the rows after
    /// <c>@after</c>; otherwise the newest by time. Parameters: <c>@n</c>, and <c>@after</c>
    /// when reading by id after a cursor.
    /// </summary>
    /// <remarks>
    /// NOLOCK because a viewer must never hold up the service writing its logs, and a
    /// dirty read of a log row costs nothing. Long columns are cut on the server, so a
    /// megabyte stack trace never crosses the network.
    /// </remarks>
    public static string Select(SqlShape shape, bool after)
    {
        Check(shape);
        var c = shape.Columns;
        var (columns, from) = (Columns(shape), From(shape));

        if (!shape.ById) return $"SELECT TOP (@n) {columns} FROM {from} ORDER BY {Quote(c.Time!)} DESC";

        var id = Quote(c.Id!);
        return after
            ? $"SELECT TOP (@n) {columns} FROM {from} WHERE {id} > @after ORDER BY {id} ASC"
            : $"SELECT TOP (@n) {columns} FROM {from} ORDER BY {id} DESC";
    }

    /// <summary>
    /// One page for the Logs page, newest first: below <c>@before</c> when <paramref name="before"/>,
    /// at <paramref name="floor"/> or worse, and holding <c>@q</c> in a text column when <paramref name="search"/>.
    /// With <paramref name="ties"/> (read by time only), every row at the time <c>@tie</c> instead.
    /// Parameters: <c>@n</c>, and <c>@before</c>, <c>@q</c>, <c>@tie</c> as asked.
    /// </summary>
    /// <remarks>
    /// The level names and numbers are written into the text: they come from the level tables,
    /// never from the user. The search is a parameter, as a LIKE pattern from <see cref="Like"/>.
    /// </remarks>
    public static string Page(SqlShape shape, bool before, bool search, LogSeverity floor, bool ties = false)
    {
        Check(shape);
        if (ties && shape.ById) throw new InvalidOperationException("Ties are for a table read by time.");

        var c = shape.Columns;
        var key = Quote(shape.ById ? c.Id! : c.Time!);
        var where = new List<string>();

        if (ties) where.Add($"{Quote(c.Time!)} = @tie");
        else if (before) where.Add($"{key} < @before");

        if (floor > LogSeverity.Unknown) where.Add(Level(shape, floor));
        if (search) where.Add(Search(c));

        var filter = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        var order = ties ? "" : $" ORDER BY {key} DESC";

        // A level or a search walks the table: on one core, so the server's own work goes on beside it.
        var hint = floor > LogSeverity.Unknown || search ? " OPTION (MAXDOP 1)" : "";
        return $"SELECT TOP (@n) {Columns(shape)} FROM {From(shape)}{filter}{order}{hint}";
    }

    /// <summary>The term as a LIKE pattern that finds it anywhere, as written: <c>% _ [</c> and the escape itself escaped (ESCAPE '\').</summary>
    public static string Like(string term) =>
        "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";

    private static readonly string[] NumberTypes = ["int", "bigint", "smallint", "tinyint", "numeric", "decimal", "float", "real"];

    private static string Level(SqlShape shape, LogSeverity floor)
    {
        // No level column: every row is "no level", which no floor lets through.
        if (shape.Columns.Level is not { } name) return "1 = 0";

        var column = Quote(name);
        var type = shape.LevelType;

        if (type is not null && NumberTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            var ranges = LogParse.LevelNumbersFrom(floor).Select(r => r.High is { } high
                ? $"{column} BETWEEN {r.Low.ToString(CultureInfo.InvariantCulture)} AND {high.ToString(CultureInfo.InvariantCulture)}"
                : $"{column} >= {r.Low.ToString(CultureInfo.InvariantCulture)}");
            return "(" + string.Join(" OR ", ranges) + ")";
        }

        // Text is compared as it is, so an index on it can still help; anything else as text —
        // text and ntext too, which can't be compared with = at all.
        var plain = type is not null && LogTableRows.IsText(type)
                    && !string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(type, "ntext", StringComparison.OrdinalIgnoreCase);
        var target = plain ? column : $"CAST({column} AS nvarchar(64))";
        var names = LogParse.LevelNamesFrom(floor).Select(n => "N'" + n.Replace("'", "''") + "'");
        return $"{target} IN ({string.Join(", ", names)})";
    }

    private static string Search(LogColumnMap c)
    {
        var columns = new[] { c.Message, c.Exception, c.Properties, c.Logger }
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // CAST because LIKE refuses xml.
        return columns.Count == 0
            ? "1 = 0"
            : "(" + string.Join(" OR ", columns.Select(n => $"CAST({Quote(n)} AS nvarchar(max)) LIKE @q ESCAPE N'\\'")) + ")";
    }

    /// <summary>Refuse a shape that can only be read by scanning: neither a usable id nor an indexed, non-text time column.</summary>
    private static void Check(SqlShape shape)
    {
        if (shape.ById) return;

        var c = shape.Columns;
        if (c.Time is null)
            throw new LogTableException(c.Id is not null && shape.IdWhole
                ? Unindexed(shape)
                : "Pick an id or a time column, so the newest rows can be found.");

        if (shape.TimeType is { } type && LogTableRows.IsText(type))
            throw new LogTableException("The time column is text, which doesn't sort by time. Pick an id column that counts up.");

        if (!shape.TimeIndexed && !shape.IsView) throw new LogTableException(Unindexed(shape));
    }

    private static string Columns(SqlShape shape)
    {
        var c = shape.Columns;
        return string.Join(", ",
            shape.ById ? $"{Quote(c.Id!)} AS k_id" : Text(c.Id, 200, "k_id"),
            shape.TimeType is { } t && LogTableRows.IsText(t) ? Text(c.Time, 64, "k_time") : Plain(c.Time, "k_time"),
            Text(c.Level, 64, "k_level"),
            Text(c.Message, LogTableRows.MessageChars, "k_message"),
            Text(c.Exception, LogTableRows.ExceptionChars, "k_exception"),
            Text(c.Properties, LogTableRows.PropertiesChars, "k_properties"),
            Text(c.Logger, LogTableRows.LoggerChars, "k_logger"));
    }

    private static string From(SqlShape shape) => $"{Quote(shape.Schema)}.{Quote(shape.Table)} WITH (NOLOCK)";

    private static string Unindexed(SqlShape shape) =>
        $"Reading {Display(shape.Schema, shape.Table)} would scan the whole table on every poll: " +
        "its id isn't an identity or a unique index, and its time column has no index. " +
        "Ask for an index on the time column, or pick a column that has one.";

    private static string Plain(string? column, string alias) =>
        column is null ? $"NULL AS {alias}" : $"{Quote(column)} AS {alias}";

    // CAST first: LEFT refuses xml and ntext, and a level may be stored as an int.
    private static string Text(string? column, int chars, string alias) =>
        column is null ? $"NULL AS {alias}" : $"LEFT(CAST({Quote(column)} AS nvarchar(max)), {chars}) AS {alias}";

    /// <summary>
    /// The mapped names, each matched to the catalog's spelling, or why one isn't there.
    /// A name the catalog doesn't list never reaches the query, quoted or not.
    /// </summary>
    public static SqlShape Resolve(string schema, string table, LogColumnMap? map, IReadOnlyList<TableColumn> columns,
                                   bool idUsable = false, bool timeIndexed = false, bool isView = false)
    {
        if (columns.Count == 0) throw new LogTableException($"There is no table {Display(schema, table)} that this login can see.");
        map ??= LogTableRows.Guess(columns);

        TableColumn? Find(string? name, string role)
        {
            if (name is null) return null;
            return columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new LogTableException($"The {role} column \"{name}\" isn't in {Display(schema, table)}.");
        }

        var id = Find(map.Id, "id");
        var time = Find(map.Time, "time");
        var level = Find(map.Level, "level");

        var resolved = new LogColumnMap
        {
            Id = id?.Name,
            Time = time?.Name,
            Level = level?.Name,
            Message = Find(map.Message, "message")?.Name,
            Exception = Find(map.Exception, "exception")?.Name,
            Properties = Find(map.Properties, "properties")?.Name,
            Logger = Find(map.Logger, "logger")?.Name,
        };

        // A decimal(18,2) id is not one to count from.
        return new SqlShape(schema, table, resolved, id?.Type, id is not null && LogTableRows.IsInteger(id),
            time?.Type, idUsable, timeIndexed, isView, level?.Type);
    }
}
