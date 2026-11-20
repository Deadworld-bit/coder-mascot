using CoderMascot.Core;
using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Driver;

// Usage (PowerShell 7, on the PC, against SCRATCH databases only) — Read-Host keeps the
// strings out of the console history, where a typed $env:… = "…" would stay:
//   $env:LOGTEST_SCRATCH = "yes"
//   $env:LOGTEST_SQL   = Read-Host -MaskInput "SQL Server connection string"
//   $env:LOGTEST_MONGO = Read-Host -MaskInput "MongoDB connection string"
//   dotnet run --project tests/LogDbTests -c Release
// Either connection may be left out; that half is skipped. Nothing is printed from either string.

int failures = 0, checks = 0;
void Check(string desc, bool ok)
{
    checks++;
    if (!ok) { failures++; Console.WriteLine($"FAIL: {desc}"); }
}

if (Environment.GetEnvironmentVariable("LOGTEST_SCRATCH") != "yes")
{
    Console.WriteLine("Refusing to run: this creates and drops tables. Point LOGTEST_SQL / LOGTEST_MONGO at scratch databases and set LOGTEST_SCRATCH=yes.");
    return 2;
}

var sql = Environment.GetEnvironmentVariable("LOGTEST_SQL");
var mongo = Environment.GetEnvironmentVariable("LOGTEST_MONGO");
var stamp = Guid.NewGuid().ToString("N")[..8];

if (!string.IsNullOrWhiteSpace(sql)) await SqlChecks(sql);
else Console.WriteLine("SQL Server: skipped (no LOGTEST_SQL)");

if (!string.IsNullOrWhiteSpace(mongo)) await MongoChecks(mongo);
else Console.WriteLine("MongoDB: skipped (no LOGTEST_MONGO)");

Console.WriteLine(failures == 0 ? $"ALL PASS ({checks} checks)" : $"{failures} of {checks} FAILED");
return failures == 0 ? 0 : 1;

async Task SqlChecks(string connection)
{
    var serilog = $"CoderMascot_LogTest_{stamp}";
    var odd = $"Log]s test {stamp}";
    var timed = $"CoderMascot_Timed_{stamp}";
    var scaled = $"CoderMascot_Scaled_{stamp}";
    var numbered = $"CoderMascot_Numbered_{stamp}";

    await using var db = new SqlConnection(connection);
    await db.OpenAsync();

    async Task Exec(string text, params SqlParameter[] parameters)
    {
        await using var command = db.CreateCommand();
        command.CommandText = text;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    try
    {
        await Exec($"""
            CREATE TABLE dbo.{LogSql.Quote(serilog)} (
              Id int IDENTITY(1,1) PRIMARY KEY, Message nvarchar(max), MessageTemplate nvarchar(max),
              Level nvarchar(128), TimeStamp datetime, Exception nvarchar(max), Properties xml)
            """);

        for (var i = 1; i <= 1200; i++)
        {
            await Exec($"INSERT INTO dbo.{LogSql.Quote(serilog)} (Message, Level, TimeStamp, Exception, Properties) VALUES (@m, @l, @t, @x, @p)",
                new SqlParameter("@m", i == 1200 ? new string('m', 50_000) : $"row {i}"),
                new SqlParameter("@l", i % 10 == 0 ? "Error" : "Information"),
                new SqlParameter("@t", new DateTime(2026, 10, 23, 10, 0, 0).AddSeconds(i)),
                new SqlParameter("@x", i % 10 == 0 ? (object)$"System.Exception: {i}\n   at X.Y()" : DBNull.Value),
                new SqlParameter("@p", $"<properties><property key='SourceContext'>Test</property><property key='N'>{i}</property></properties>"));
        }

        var source = new LogSourceEntry { Kind = LogSourceKinds.SqlServer, Table = $"dbo.{serilog}" };
        var probe = await SqlLogReader.ProbeAsync(connection, source, CancellationToken.None);
        Check($"sql probe: no problem ({probe.Problem})", probe.Problem is null);
        Check("sql probe: lists the table", probe.Tables.Contains($"dbo.{serilog}"));
        Check("sql probe: columns and a preview", probe.Columns.Count == 7 && probe.Preview.Count == 5);
        Check("sql probe: the guess", LogTableRows.Guess(probe.Columns) is { Id: "Id", Time: "TimeStamp", Message: "Message", Properties: "Properties" });

        await using (var reader = new SqlLogReader(connection, source))
        {
            var first = await reader.ReadAsync(null, 1000, CancellationToken.None);
            Check("sql: newest 1,000, oldest first", first.Rows.Count == 1000 && first.Rows[0].Number == 201 && first.Rows[^1].Number == 1200 && !first.Window);
            Check("sql: long values cut on the server", first.Rows[^1].Message!.Length == LogTableRows.MessageChars);

            var entry = LogTableRows.ToEntry(first.Rows.First(r => r.Number == 1190), source.Table!, false);
            Check("sql: an error row maps", entry.Level == LogSeverity.Error && entry.Detail!.Contains("System.Exception: 1190")
                && entry.Detail.Contains("SourceContext: Test"));

            var none = await reader.ReadAsync("1200", 500, CancellationToken.None);
            Check("sql: nothing after the newest", none.Rows.Count == 0);

            await Exec($"INSERT INTO dbo.{LogSql.Quote(serilog)} (Message, Level, TimeStamp) VALUES ('late', 'Warning', GETDATE())");
            var late = await reader.ReadAsync("1200", 500, CancellationToken.None);
            Check("sql: a new row after the cursor", late.Rows.Count == 1 && late.Rows[0].Message == "late");

            // Pages: newest first, each below the one above, the level and the search done by the server.
            var page1 = await reader.PageAsync(new TablePageQuery(100), CancellationToken.None);
            var page2 = await reader.PageAsync(new TablePageQuery(100, page1.Older), CancellationToken.None);
            Check("sql page: newest first, the next below it", page1.Rows.Count == 100 && page1.Rows[0].Number == 1201 && page1.Rows[^1].Number == 1102
                && page2.Rows[0].Number == 1101 && page2.Rows[^1].Number == 1002);

            var errors = await reader.PageAsync(new TablePageQuery(50, null, LogSeverity.Error), CancellationToken.None);
            Check("sql page: errors only, filtered by the server", errors.Rows.Count == 50 && errors.Rows[0].Number == 1200
                && errors.Rows.All(r => LogParse.LevelOf(r.Level) == LogSeverity.Error) && errors.Older is not null);

            var warned = await reader.PageAsync(new TablePageQuery(3, null, LogSeverity.Warning), CancellationToken.None);
            Check("sql page: warnings and up", warned.Rows.Select(r => r.Number).SequenceEqual(new long?[] { 1201, 1200, 1190 }));

            var inException = await reader.PageAsync(new TablePageQuery(10, null, LogSeverity.Unknown, "System.Exception: 119"), CancellationToken.None);
            Check("sql page: the search reads the exception column", inException.Rows.Select(r => r.Number).SequenceEqual(new long?[] { 1190 }) && inException.Older is null);

            var inXml = await reader.PageAsync(new TablePageQuery(10, null, LogSeverity.Unknown, ">1150<"), CancellationToken.None);
            Check("sql page: and the xml properties", inXml.Rows.Select(r => r.Number).SequenceEqual(new long?[] { 1150 }));

            await Exec($"INSERT INTO dbo.{LogSql.Quote(serilog)} (Message, Level, TimeStamp) VALUES (N'50%_[off] sale', 'Information', GETDATE()), (N'50 apples offered', 'Information', GETDATE())");
            var literal = await reader.PageAsync(new TablePageQuery(10, null, LogSeverity.Unknown, "50%_[off]"), CancellationToken.None);
            Check("sql page: % _ [ are searched as written", literal.Rows.Count == 1 && literal.Rows[0].Message == "50%_[off] sale");
        }

        await Exec($"CREATE TABLE dbo.{LogSql.Quote(odd)} ({LogSql.Quote("Row Id")} bigint IDENTITY PRIMARY KEY, {LogSql.Quote("My ]Message")} nvarchar(200), {LogSql.Quote("When")} datetime2)");
        await Exec($"INSERT INTO dbo.{LogSql.Quote(odd)} ({LogSql.Quote("My ]Message")}, {LogSql.Quote("When")}) VALUES ('odd one', SYSDATETIME())");
        var oddSource = new LogSourceEntry
        {
            Kind = LogSourceKinds.SqlServer, Table = $"dbo.{LogSql.Quote(odd)}",
            Columns = new LogColumnMap { Id = "Row Id", Message = "My ]Message", Time = "When" },
        };
        await using (var reader = new SqlLogReader(connection, oddSource))
        {
            var rows = await reader.ReadAsync(null, 10, CancellationToken.None);
            Check("sql: names with spaces and brackets", rows.Rows.Count == 1 && rows.Rows[0].Message == "odd one");
        }

        await Exec($"CREATE TABLE dbo.{LogSql.Quote(timed)} (Content nvarchar(200), CreatedDate datetime)");
        for (var i = 1; i <= 30; i++)
            await Exec($"INSERT INTO dbo.{LogSql.Quote(timed)} VALUES (@c, @d)",
                new SqlParameter("@c", $"t{i}"), new SqlParameter("@d", new DateTime(2026, 10, 23).AddMinutes(i)));
        var timedSource = new LogSourceEntry { Kind = LogSourceKinds.SqlServer, Table = timed };
        try
        {
            await using var unindexed = new SqlLogReader(connection, timedSource);
            await unindexed.ReadAsync(null, 10, CancellationToken.None);
            Check("sql: no id and no index on the time column is refused", false);
        }
        catch (LogTableException ex)
        {
            Check($"sql: no id and no index on the time column is refused ({ex.Message})", ex.Message.Contains("scan the whole table"));
        }

        await Exec($"CREATE INDEX IX_Created ON dbo.{LogSql.Quote(timed)} (CreatedDate)");
        await using (var reader = new SqlLogReader(connection, timedSource))
        {
            var rows = await reader.ReadAsync("5", 10, CancellationToken.None);
            Check("sql: no id → the newest by an indexed time, in window mode", rows.Window && rows.Rows.Count == 10 && rows.Rows[^1].Message == "t30");

            // Five more at t10's time: a page whose last row is at that time takes all six.
            await Exec($"INSERT INTO dbo.{LogSql.Quote(timed)} VALUES ('tie a', @d), ('tie b', @d), ('tie c', @d), ('tie d', @d), ('tie e', @d)",
                new SqlParameter("@d", new DateTime(2026, 10, 23).AddMinutes(10)));
            var top = await reader.PageAsync(new TablePageQuery(21), CancellationToken.None);
            var rest = await reader.PageAsync(new TablePageQuery(21, top.Older), CancellationToken.None);
            var seen = top.Rows.Concat(rest.Rows).Select(r => r.Message).ToList();
            Check($"sql page by time: rows that share the boundary time stay together ({top.Rows.Count} + {rest.Rows.Count})",
                top.Rows.Count == 26 && rest.Rows.Count == 9 && rest.Older is null && seen.Distinct().Count() == 35 && !top.TiesCut);
        }

        await Exec($"CREATE TABLE dbo.{LogSql.Quote(numbered)} (Id bigint IDENTITY PRIMARY KEY, Level int, Message nvarchar(100), TimeStamp datetime2)");
        await Exec($"INSERT INTO dbo.{LogSql.Quote(numbered)} (Level, Message, TimeStamp) VALUES (40000, 'info', SYSDATETIME()), (70000, 'error', SYSDATETIME()), (4, 'mel error', SYSDATETIME()), (2, 'mel info', SYSDATETIME())");
        await using (var reader = new SqlLogReader(connection, new LogSourceEntry
        {
            Kind = LogSourceKinds.SqlServer, Table = numbered,
            Columns = new LogColumnMap { Id = "Id", Level = "Level", Message = "Message", Time = "TimeStamp" },
        }))
        {
            var numberErrors = await reader.PageAsync(new TablePageQuery(10, null, LogSeverity.Error), CancellationToken.None);
            Check("sql page: a number level by range", numberErrors.Rows.Select(r => r.Message).SequenceEqual(["mel error", "error"]));
        }

        await Exec($"CREATE TABLE dbo.{LogSql.Quote(scaled)} (Id decimal(18,2) PRIMARY KEY, Message nvarchar(100), TimeStamp datetime)");
        await Exec($"CREATE INDEX IX_Time ON dbo.{LogSql.Quote(scaled)} (TimeStamp)");
        await Exec($"INSERT INTO dbo.{LogSql.Quote(scaled)} VALUES (1.50, 'a', '2026-10-23 10:00'), (2.50, 'b', '2026-10-23 10:01')");
        await using (var reader = new SqlLogReader(connection, new LogSourceEntry { Kind = LogSourceKinds.SqlServer, Table = scaled }))
        {
            var rows = await reader.ReadAsync(null, 10, CancellationToken.None);
            Check("sql: a decimal(18,2) id isn't counted from; read by time", rows.Window && rows.Rows.Count == 2 && rows.Rows[^1].Message == "b");
        }

        var wrong = new SqlConnectionStringBuilder(connection) { Password = "Wrong-Pass-77391" }.ConnectionString;
        try
        {
            await using var reader = new SqlLogReader(wrong, source);
            await reader.ReadAsync(null, 1, CancellationToken.None);
            Check("sql: a wrong password fails", false);
        }
        catch (LogTableException ex)
        {
            Check($"sql: the failure names no password ({ex.Message})", !ex.Message.Contains("Wrong-Pass-77391"));
        }

        Check("sql: a broken connection string is said without its text",
            Message(() => new SqlLogReader("Server=x;Password=Secret-123;Bogus Key=1", source)) is { } m && !m.Contains("Secret-123"));
    }
    finally
    {
        foreach (var table in new[] { serilog, odd, timed, scaled, numbered })
        {
            try { await Exec($"IF OBJECT_ID(@t) IS NOT NULL DROP TABLE dbo.{LogSql.Quote(table)}", new SqlParameter("@t", $"dbo.{LogSql.Quote(table)}")); }
            catch (SqlException) { Console.WriteLine($"  could not drop {table} — drop it by hand"); }
        }
    }
}

async Task MongoChecks(string connection)
{
    MongoUrl url;
    try
    {
        url = new MongoUrl(connection);
    }
    catch (Exception ex)
    {
        // The driver's own message quotes the whole string, password included.
        Console.WriteLine($"MongoDB: LOGTEST_MONGO doesn't parse ({ex.GetType().Name}); skipped");
        failures++;
        return;
    }

    var client = new MongoClient(url);
    var db = client.GetDatabase(url.DatabaseName ?? "scratch");
    var name = $"coder_mascot_logtest_{stamp}";
    var stringIds = $"coder_mascot_strings_{stamp}";

    try
    {
        var logs = db.GetCollection<BsonDocument>(name);
        var start = new DateTime(2026, 10, 23, 3, 0, 0, DateTimeKind.Utc);
        await logs.InsertManyAsync(Enumerable.Range(1, 1200).Select(i => new BsonDocument
        {
            { "Timestamp", start.AddSeconds(i) },
            { "Level", i % 10 == 0 ? "Error" : "Information" },
            { "RenderedMessage", $"doc {i}" },
            { "Exception", i % 10 == 0 ? (BsonValue)$"System.Exception: {i}" : BsonNull.Value },
            { "Properties", new BsonDocument { { "SourceContext", "Test" }, { "N", i } } },
        }));

        var source = new LogSourceEntry { Kind = LogSourceKinds.Mongo, Table = name, Database = db.DatabaseNamespace.DatabaseName };
        var probe = await MongoLogReader.ProbeAsync(connection, source, CancellationToken.None);
        Check($"mongo probe: no problem ({probe.Problem})", probe.Problem is null);
        Check("mongo probe: lists the collection, fields and a preview", probe.Tables.Contains(name) && probe.Columns.Any(c => c.Name == "RenderedMessage")
            && probe.Preview.Count == 5);

        await using (var reader = new MongoLogReader(connection, source))
        {
            var first = await reader.ReadAsync(null, 1000, CancellationToken.None);
            Check("mongo: newest 1,000, oldest first", first.Rows.Count == 1000 && first.Rows[^1].Message == "doc 1200" && !first.Window);

            var entry = LogTableRows.ToEntry(first.Rows.First(r => r.Message == "doc 1190"), name, false);
            Check("mongo: an error document maps, UTC kept", entry.Level == LogSeverity.Error && entry.Time == new DateTimeOffset(start.AddSeconds(1190))
                && entry.Detail!.Contains("SourceContext: Test"));

            var cursor = first.Rows[^1].Cursor;
            Check("mongo: nothing after the newest", (await reader.ReadAsync(cursor, 500, CancellationToken.None)).Rows.Count == 0);

            await logs.InsertOneAsync(new BsonDocument { { "Timestamp", DateTime.UtcNow }, { "Level", "Warning" }, { "RenderedMessage", "late" } });
            var late = await reader.ReadAsync(cursor, 500, CancellationToken.None);
            Check("mongo: a new document after the cursor", late.Rows.Count == 1 && late.Rows[0].Message == "late");

            var page1 = await reader.PageAsync(new TablePageQuery(100), CancellationToken.None);
            var page2 = await reader.PageAsync(new TablePageQuery(100, page1.Older), CancellationToken.None);
            Check("mongo page: newest first, the next below it", page1.Rows.Count == 100 && page1.Rows[0].Message == "late"
                && page1.Rows[^1].Message == "doc 1102" && page2.Rows[0].Message == "doc 1101" && page2.Rows[^1].Message == "doc 1002");

            var errors = await reader.PageAsync(new TablePageQuery(5, null, LogSeverity.Warning), CancellationToken.None);
            Check("mongo page: warnings and up, filtered by the server", errors.Rows.Select(r => r.Message).SequenceEqual(["late", "doc 1200", "doc 1190", "doc 1180", "doc 1170"]));

            var found = await reader.PageAsync(new TablePageQuery(10, null, LogSeverity.Unknown, "SYSTEM.EXCEPTION: 119"), CancellationToken.None);
            Check("mongo page: the search ignores case and reads the exception field", found.Rows.Select(r => r.Message).SequenceEqual(["doc 1190"]));

            await logs.InsertOneAsync(new BsonDocument { { "Timestamp", DateTime.UtcNow }, { "Level", "Information" }, { "RenderedMessage", "price (a+b)*2 [x]" } });
            var literal = await reader.PageAsync(new TablePageQuery(10, null, LogSeverity.Unknown, "(a+b)*2 [x]"), CancellationToken.None);
            Check("mongo page: a regex character is searched as written", literal.Rows.Count == 1 && literal.Rows[0].Message == "price (a+b)*2 [x]");
        }

        var strings = db.GetCollection<BsonDocument>(stringIds);
        await strings.InsertManyAsync(Enumerable.Range(1, 20).Select(i => new BsonDocument
        {
            { "_id", $"id-{i:000}" }, { "Timestamp", start.AddMinutes(i) }, { "RenderedMessage", $"s{i}" },
        }));
        var stringSource = new LogSourceEntry
        {
            Kind = LogSourceKinds.Mongo, Table = stringIds, Database = db.DatabaseNamespace.DatabaseName,
            Columns = new LogColumnMap { Time = "Timestamp", Message = "RenderedMessage" },
        };

        try
        {
            await using var unindexed = new MongoLogReader(connection, stringSource);
            await unindexed.ReadAsync(null, 5, CancellationToken.None);
            Check("mongo: string ids and no index on the time field is refused", false);
        }
        catch (LogTableException ex)
        {
            Check($"mongo: string ids and no index on the time field is refused ({ex.Message})", ex.Message.Contains("no index"));
        }

        await strings.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Descending("Timestamp")));
        await using (var reader = new MongoLogReader(connection, stringSource))
        {
            var rows = await reader.ReadAsync(null, 5, CancellationToken.None);
            Check("mongo: string ids → newest by an indexed time, window mode", rows.Window && rows.Rows.Count == 5 && rows.Rows[^1].Message == "s20");

            await strings.InsertManyAsync(Enumerable.Range(1, 3).Select(i => new BsonDocument
            {
                { "_id", $"tie-{i}" }, { "Timestamp", start.AddMinutes(10) }, { "RenderedMessage", $"tie {i}" },
            }));
            var top = await reader.PageAsync(new TablePageQuery(11), CancellationToken.None);
            var rest = await reader.PageAsync(new TablePageQuery(11, top.Older), CancellationToken.None);
            Check($"mongo page by time: documents that share the boundary time stay together ({top.Rows.Count} + {rest.Rows.Count})",
                top.Rows.Count == 14 && rest.Rows.Count == 9 && rest.Older is null && top.Rows.Concat(rest.Rows).Select(r => r.Message).Distinct().Count() == 23);
        }

        Check("mongo: a field inside another included field is left out of the projection",
            MongoLogReader.Fields(new LogColumnMap { Properties = "Properties", Logger = "Properties.SourceContext", Message = "M" })
                .SequenceEqual(["M", "Properties"]));

        var wrong = new MongoUrlBuilder(url.ToString()) { Password = "Wrong-Pass-77391" };
        if (!string.IsNullOrEmpty(url.Username))
        {
            try
            {
                await using var reader = new MongoLogReader(wrong.ToString(), source);
                await reader.ReadAsync(null, 1, CancellationToken.None);
                Check("mongo: a wrong password fails", false);
            }
            catch (LogTableException ex)
            {
                Check($"mongo: the failure names no password ({ex.Message})", !ex.Message.Contains("Wrong-Pass-77391"));
            }
        }

        Check("mongo: a broken connection string is said without its text",
            Message(() => new MongoLogReader("mongodb://u:Secret-123@", source)) is { } m && !m.Contains("Secret-123"));
    }
    finally
    {
        await db.DropCollectionAsync(name);
        await db.DropCollectionAsync(stringIds);
        client.Dispose();
    }
}

static string? Message(Action act)
{
    try { act(); return null; }
    catch (LogTableException ex) { return ex.Message; }
}
