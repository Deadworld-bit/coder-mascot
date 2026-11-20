using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace CoderMascot.Core;

/// <summary>
/// Reads a log table on SQL Server. SELECT only; see <see cref="LogSql"/> for the
/// text and why it is shaped the way it is. The login it is given should be one
/// that can read this table and nothing else — the reader never writes, but a
/// connection string with a full-rights login is a key left on a desktop.
/// </summary>
public sealed class SqlLogReader(string connection, LogSourceEntry source) : ILogTableReader
{
    private const int CommandSeconds = 15;

    /// <summary>The most one page may take, its queries together: a command's own timeout counts each read on its own.</summary>
    private const int PageSeconds = 20;

    private readonly string _connection = Prepare(connection);
    private SqlShape? _shape;

    /// <summary>
    /// The connection string with the reader's own limits: a short connect timeout, read-only
    /// intent, and a name a DBA can recognise in sp_who. Throws <see cref="LogTableException"/>
    /// for one that won't parse — without its text, which holds the password — and for one
    /// that would make the server attach a database file, which is a write.
    /// </summary>
    public static string Prepare(string raw)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(raw)
            {
                ApplicationIntent = ApplicationIntent.ReadOnly,
                ApplicationName = "CoderMascot (log viewer)",
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new LogTableException("The connection string isn't valid.");
        }

        if (!string.IsNullOrEmpty(builder.AttachDBFilename) || builder.UserInstance)
            throw new LogTableException("The connection string attaches a database file; a log viewer only reads.");

        // 0 means "wait for ever" to SqlClient.
        if (builder.ConnectTimeout is <= 0 or > 15) builder.ConnectTimeout = 15;
        builder.CommandTimeout = CommandSeconds;
        return builder.ConnectionString;
    }

    /// <summary>"server / database", for display; never the credentials — nor what a missing ';' glued onto the server name.</summary>
    public static string? HostOf(string raw)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(raw);
            var server = builder.DataSource;
            if (string.IsNullOrWhiteSpace(server) || server.IndexOfAny(['=', ';', '@', ' ']) >= 0) return null;

            var database = builder.InitialCatalog;
            return string.IsNullOrWhiteSpace(database) || database.IndexOfAny(['=', ';']) >= 0 ? server : $"{server} / {database}";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>What about this connection string is worth a second look, or null. Shown in the form, never blocking.</summary>
    public static string? Warning(string raw)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(raw);
            if (builder.TrustServerCertificate)
                return "This connection doesn't check the server's certificate (TrustServerCertificate=True): anyone on the network path could pose as the server.";
            if (builder.Encrypt != SqlConnectionEncryptOption.Mandatory && builder.Encrypt != SqlConnectionEncryptOption.Strict)
                return "This connection isn't required to be encrypted (Encrypt is not Mandatory or Strict).";
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<TableRead> ReadAsync(string? after, int max, CancellationToken ct)
    {
        try
        {
            await using var db = new SqlConnection(_connection);
            await db.OpenAsync(ct).ConfigureAwait(false);

            _shape ??= await ShapeAsync(db, source, ct).ConfigureAwait(false);
            var window = !_shape.ById;

            // A cursor that doesn't parse reads the newest again; the caller's dedupe absorbs the overlap.
            decimal cursor = 0;
            var incremental = !window && after is not null
                              && decimal.TryParse(after, NumberStyles.Number, CultureInfo.InvariantCulture, out cursor);

            await using var command = db.CreateCommand();
            command.CommandText = LogSql.Select(_shape, incremental);
            command.CommandTimeout = CommandSeconds;
            command.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = Math.Clamp(max, 1, 5000) });

            if (incremental) command.Parameters.Add(IdParameter("@after", _shape, cursor));

            var rows = new List<TableRow>();
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    rows.Add(Row(reader, window, rows.Count));
            }

            // The newest-first reads come back newest first; everything leaves here oldest first.
            if (!incremental) rows.Reverse();
            return new TableRead(rows, window);
        }
        catch (SqlException ex)
        {
            throw new LogTableException(Describe(ex));
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            throw new LogTableException("SQL Server couldn't be reached with this connection string.");
        }
        catch (OverflowException)
        {
            throw new LogTableException("An id in this table is too large to read.");
        }
    }

    public async Task<TablePage> PageAsync(TablePageQuery query, CancellationToken asked)
    {
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(asked);
        wall.CancelAfter(TimeSpan.FromSeconds(PageSeconds));
        var ct = wall.Token;

        try
        {
            await using var db = new SqlConnection(_connection);
            await db.OpenAsync(ct).ConfigureAwait(false);

            var shape = _shape ??= await ShapeAsync(db, source, ct).ConfigureAwait(false);
            if (query.Floor > LogSeverity.Unknown && shape.Columns.Level is null) return new TablePage([], null);

            var size = Math.Clamp(query.Size, 1, LogTableRows.MaxPageRows);
            var term = LogTableRows.SearchTerm(query.Search);
            var window = !shape.ById;

            // One more than the page, to know whether there is a page below it.
            var rows = await PageRowsAsync(db, shape, LogSql.Page(shape, query.Before is not null, term is not null, query.Floor),
                size + 1, query.Before, tie: null, term, ct).ConfigureAwait(false);

            if (rows.Count <= size) return new TablePage(rows, null);
            var next = rows[size];
            rows.RemoveAt(size);
            if (!window) return new TablePage(rows, rows[^1].Cursor);

            // NULL times sort last and can't be paged below.
            if (rows[^1].Time is not { } tie || next.Time is null) return new TablePage(rows, null);

            // The next row is older: every row at this page's last time is on it already.
            if (!Equals(next.Time, tie)) return new TablePage(rows, tie);

            // Rows share the time, and "below this time" would skip the ones at it that missed this page: take them all here.

            var ties = await PageRowsAsync(db, shape, LogSql.Page(shape, false, term is not null, query.Floor, ties: true),
                LogTableRows.MaxTies + 1, before: null, tie, term, ct).ConfigureAwait(false);

            var cut = ties.Count > LogTableRows.MaxTies;
            if (cut) ties.RemoveAt(LogTableRows.MaxTies);

            rows.RemoveAll(r => Equals(r.Time, tie));
            rows.AddRange(ties);
            return new TablePage(rows, tie, cut);
        }
        catch (Exception) when (wall.IsCancellationRequested && !asked.IsCancellationRequested)
        {
            throw new LogTableException(LogTableRows.SlowPage, slow: true);
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            throw new LogTableException(LogTableRows.SlowPage, slow: true);
        }
        catch (SqlException ex)
        {
            throw new LogTableException(Describe(ex));
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            throw new LogTableException("SQL Server couldn't be reached with this connection string.");
        }
        catch (OverflowException)
        {
            throw new LogTableException("An id in this table is too large to read.");
        }
    }

    private static async Task<List<TableRow>> PageRowsAsync(SqlConnection db, SqlShape shape, string sql, int n,
                                                            object? before, object? tie, string? term, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandSeconds;
        command.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = n });

        if (before is not null)
        {
            if (!shape.ById) command.Parameters.Add(TimeParameter("@before", shape, before));
            else if (before is string id && decimal.TryParse(id, NumberStyles.Number, CultureInfo.InvariantCulture, out var cursor))
                command.Parameters.Add(IdParameter("@before", shape, cursor));
            else throw new LogTableException("That page can't be found any more. Go back to the newest page.");
        }

        if (tie is not null) command.Parameters.Add(TimeParameter("@tie", shape, tie));
        if (term is not null) command.Parameters.Add(new SqlParameter("@q", SqlDbType.NVarChar, 4000) { Value = LogSql.Like(term) });

        var rows = new List<TableRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(Row(reader, !shape.ById, rows.Count));

        return rows;
    }

    // Typed like the column, or SQL Server converts the column and the seek becomes a scan.
    private static SqlParameter IdParameter(string name, SqlShape shape, decimal value) =>
        shape.IdType?.ToLowerInvariant() is "numeric" or "decimal"
            ? new SqlParameter(name, SqlDbType.Decimal) { Precision = 38, Scale = 0, Value = value }
            : new SqlParameter(name, SqlDbType.BigInt) { Value = (long)Math.Clamp(value, long.MinValue, long.MaxValue) };

    // Typed like the column too: a datetime2 boundary sent as datetime is rounded to 3 ms, and the rows at the edge are lost or shown twice.
    private static SqlParameter TimeParameter(string name, SqlShape shape, object value) => value switch
    {
        // An id cursor kept from before the source was edited to read by time.
        string => throw new LogTableException("That page can't be found any more. Go back to the newest page."),
        DateTimeOffset => new SqlParameter(name, SqlDbType.DateTimeOffset) { Scale = 7, Value = value },
        DateTime => shape.TimeType?.ToLowerInvariant() switch
        {
            "datetime2" => new SqlParameter(name, SqlDbType.DateTime2) { Scale = 7, Value = value },
            "smalldatetime" => new SqlParameter(name, SqlDbType.SmallDateTime) { Value = value },
            "date" => new SqlParameter(name, SqlDbType.Date) { Value = value },
            _ => new SqlParameter(name, SqlDbType.DateTime) { Value = value },
        },

        // A number standing for a time: sent as the number it is.
        _ => new SqlParameter(name, value),
    };

    private static TableRow Row(SqlDataReader reader, bool window, int index)
    {
        var id = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
        long? number = long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

        object? Value(int i) => reader.IsDBNull(i) ? null : reader.GetValue(i);
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

        var time = Value(1);
        var message = Text(3);

        // Window rows are replaced each read; the key only has to tell them apart within one,
        // and stays short — a message can be 8,000 characters.
        var key = window || id is null
            ? $"{(time as DateTime?)?.Ticks ?? (time as DateTimeOffset?)?.UtcTicks ?? 0}|{index}|{message?.GetHashCode() ?? 0}"
            : id;

        return new TableRow(key, window ? null : id, number, time, Text(2), message, Text(4), Text(5), Text(6));
    }

    /// <summary>The table's real column names matched against the mapping, and how the table can be read cheaply.</summary>
    private static async Task<SqlShape> ShapeAsync(SqlConnection db, LogSourceEntry source, CancellationToken ct)
    {
        if (LogSql.SplitTable(source.Table) is not { } name)
            throw new LogTableException("The table name isn't valid. Write it as schema.table, like dbo.Logs.");

        var (schema, table) = name;
        var columns = await ColumnsAsync(db, schema, table, ct).ConfigureAwait(false);
        var shape = LogSql.Resolve(schema, table, source.Columns, columns);

        await using var command = db.CreateCommand();
        command.CommandText = LogSql.IndexQuery;
        command.CommandTimeout = CommandSeconds;
        command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = shape.Schema });
        command.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 128) { Value = shape.Table });
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 128) { Value = (object?)shape.Columns.Id ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@time", SqlDbType.NVarChar, 128) { Value = (object?)shape.Columns.Time ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return shape;

        return shape with
        {
            IsView = reader.GetInt32(0) == 1,
            IdUsable = reader.GetInt32(1) == 1,
            TimeIndexed = reader.GetInt32(2) == 1,
        };
    }

    private static async Task<IReadOnlyList<TableColumn>> ColumnsAsync(SqlConnection db, string schema, string table, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = LogSql.ColumnsQuery;
        command.CommandTimeout = CommandSeconds;
        command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = schema });
        command.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 128) { Value = table });

        var columns = new List<TableColumn>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            int? scale = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
            columns.Add(new TableColumn(reader.GetString(0), reader.GetString(1), scale));
        }

        return columns;
    }

    /// <summary>For the form: does it connect, which tables are there, what's in the chosen one.</summary>
    public static async Task<TableProbe> ProbeAsync(string connection, LogSourceEntry draft, CancellationToken ct)
    {
        var host = HostOf(connection);

        try
        {
            var prepared = Prepare(connection);
            await using var db = new SqlConnection(prepared);
            await db.OpenAsync(ct).ConfigureAwait(false);

            var tables = new List<string>();
            await using (var command = db.CreateCommand())
            {
                command.CommandText = LogSql.TablesQuery;
                command.CommandTimeout = CommandSeconds;
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false) && tables.Count < 1000)
                    tables.Add(LogSql.Display(reader.GetString(0), reader.GetString(1)));
            }

            IReadOnlyList<TableColumn> columns = [];
            IReadOnlyList<LogEntry> preview = [];
            string? problem = null;

            if (LogSql.SplitTable(draft.Table) is { } name)
            {
                var (schema, table) = name;
                columns = await ColumnsAsync(db, schema, table, ct).ConfigureAwait(false);

                // The connection and the table list are good news even when the mapping isn't yet.
                try
                {
                    if (columns.Count > 0)
                    {
                        await using var reader = new SqlLogReader(connection, draft);
                        var read = await reader.ReadAsync(null, 5, ct).ConfigureAwait(false);
                        preview = read.Rows.Select(r => LogTableRows.ToEntry(r, table, draft.Utc == true)).ToList();
                    }
                }
                catch (LogTableException ex)
                {
                    problem = ex.Message;
                }
            }

            return new TableProbe(problem, host, tables, columns, preview);
        }
        catch (LogTableException ex)
        {
            return new TableProbe(ex.Message, host, [], [], []);
        }
        catch (SqlException ex)
        {
            return new TableProbe(Describe(ex), host, [], [], []);
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            return new TableProbe("SQL Server couldn't be reached with this connection string.", host, [], [], []);
        }
    }

    private static readonly Regex Secret = new(@"(password|pwd)\s*=\s*(""[^""]*""|'[^']*'|[^;]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// SQL Server's own sentence — "Login failed for user 'x'", "Invalid object name" — is
    /// the useful part, so it is kept, first line only, with anything shaped like a password cut out.
    /// </summary>
    private static string Describe(SqlException ex)
    {
        var text = ex.Message.Split('\n')[0].Trim();
        try
        {
            text = Secret.Replace(text, "$1=…");
        }
        catch (RegexMatchTimeoutException)
        {
            text = "SQL Server refused the request.";
        }

        if (text.Length > 300) text = text[..300] + "…";
        return $"SQL Server: {text}";
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
