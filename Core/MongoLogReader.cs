using System.Buffers.Binary;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace CoderMascot.Core;

/// <summary>
/// Reads a log collection on MongoDB, by <c>_id</c>: an ObjectId or a number counts up,
/// so "after the last one read" is a range query on the one index every collection has.
/// Anything else is read as the newest by time — only when the time field is indexed.
///
/// Its exceptions are never passed on as they are: the driver's messages can quote the
/// whole connection string, password included.
/// </summary>
public sealed class MongoLogReader : ILogTableReader
{
    private const int MaxSeconds = 15;

    /// <summary>
    /// ObjectIds made in the same second by different processes don't come in order, so
    /// "after the last one" starts this much earlier; rows already held are skipped by key.
    /// </summary>
    private const int OverlapSeconds = 5;

    private const int KeyChars = 200;

    private static readonly JsonWriterSettings Json = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    private readonly MongoClient _client;
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly LogColumnMap _map;

    /// <summary>Whether <c>_id</c> counts up; decided once, from one document.</summary>
    private bool? _counts;

    public MongoLogReader(string connection, LogSourceEntry source)
    {
        _map = source.Columns ?? new LogColumnMap { Id = "_id", Time = "Timestamp", Level = "Level", Message = "RenderedMessage", Exception = "Exception", Properties = "Properties" };

        MongoClient? client = null;
        try
        {
            // new, not MongoUrl.Create: Create keeps every URL it parses — password and all — in a static cache.
            var url = new MongoUrl(connection);
            var database = source.Database ?? url.DatabaseName;
            if (string.IsNullOrWhiteSpace(database)) throw new LogTableException("Name the database: the connection string doesn't.");
            if (LogSourceRules.DatabaseProblem(database) is { } bad) throw new LogTableException(bad);
            if (string.IsNullOrWhiteSpace(source.Table)) throw new LogTableException("Name the collection.");

            client = new MongoClient(Settings(url));
            _collection = client.GetDatabase(database).GetCollection<BsonDocument>(source.Table);
            _client = client;
        }
        catch (Exception ex)
        {
            client?.Dispose();
            throw ex as LogTableException ?? new LogTableException(Describe(ex));
        }
    }

    private static MongoClientSettings Settings(MongoUrl url)
    {
        var settings = MongoClientSettings.FromUrl(url);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        settings.ConnectTimeout = TimeSpan.FromSeconds(10);
        settings.SocketTimeout = TimeSpan.FromSeconds(20);
        settings.ReadPreference = ReadPreference.SecondaryPreferred;
        settings.RetryWrites = false;
        settings.ApplicationName = "CoderMascot (log viewer)";
        return settings;
    }

    /// <summary>"server / database", for display; never the credentials.</summary>
    public static string? HostOf(string raw, string? database)
    {
        try
        {
            var url = new MongoUrl(raw);
            var servers = string.Join(",", url.Servers.Select(s => s.ToString()));
            var name = database ?? url.DatabaseName;
            return string.IsNullOrWhiteSpace(name) ? servers : $"{servers} / {name}";
        }
        catch (Exception ex) when (ex is MongoConfigurationException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>What about this connection string is worth a second look, or null. Shown in the form, never blocking.</summary>
    public static string? Warning(string raw)
    {
        try
        {
            var url = new MongoUrl(raw);
            if (url.AllowInsecureTls) return "This connection doesn't check the server's certificate (tlsInsecure).";
            if (!url.UseTls) return "This connection isn't encrypted (TLS is off): the password and the logs cross the network in clear.";
            return null;
        }
        catch (Exception ex) when (ex is MongoConfigurationException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    public async Task<TableRead> ReadAsync(string? after, int max, CancellationToken ct)
    {
        try
        {
            var limit = Math.Clamp(max, 1, 5000);
            var projection = Projection(_map);

            _counts ??= await CountsUpAsync(ct).ConfigureAwait(false);

            if (_counts == true && after is not null && From(after) is { } from)
            {
                var newer = await RowsAsync(Builders<BsonDocument>.Filter.Gt("_id", from), projection,
                    Builders<BsonDocument>.Sort.Ascending("_id"), limit, window: false, ct).ConfigureAwait(false);
                return new TableRead(newer, Window: false);
            }

            if (_counts == true)
            {
                var newest = await RowsAsync(FilterDefinition<BsonDocument>.Empty, projection,
                    Builders<BsonDocument>.Sort.Descending("_id"), limit, window: false, ct).ConfigureAwait(false);
                newest.Reverse();
                return new TableRead(newest, Window: false);
            }

            // An _id that doesn't count up can't say what is newer: the newest by time, if that is cheap.
            var time = await TimeFieldAsync(ct).ConfigureAwait(false);
            var byTime = await RowsAsync(FilterDefinition<BsonDocument>.Empty, projection,
                Builders<BsonDocument>.Sort.Descending(time), limit, window: true, ct).ConfigureAwait(false);
            byTime.Reverse();
            return new TableRead(byTime, Window: true);
        }
        catch (Exception ex) when (ex is not LogTableException and not OperationCanceledException and not OutOfMemoryException)
        {
            throw new LogTableException(Describe(ex));
        }
    }

    public async Task<TablePage> PageAsync(TablePageQuery query, CancellationToken ct)
    {
        try
        {
            if (query.Floor > LogSeverity.Unknown && _map.Level is null) return new TablePage([], null);

            var f = Builders<BsonDocument>.Filter;
            var size = Math.Clamp(query.Size, 1, LogTableRows.MaxPageRows);
            var term = LogTableRows.SearchTerm(query.Search);
            var projection = Projection(_map);

            var filters = new List<FilterDefinition<BsonDocument>>();
            if (query.Floor > LogSeverity.Unknown) filters.Add(LevelFilter(_map.Level!, query.Floor));
            if (term is not null) filters.Add(SearchFilter(_map, term));

            _counts ??= await CountsUpAsync(ct).ConfigureAwait(false);

            if (_counts == true)
            {
                var below = new List<FilterDefinition<BsonDocument>>(filters);
                if (query.Before is not null) below.Add(f.Lt("_id", Exact(query.Before) ?? throw Lost()));

                // One more than the page, to know whether there is a page below it.
                var rows = await RowsAsync(All(below), projection, Builders<BsonDocument>.Sort.Descending("_id"),
                    size + 1, window: false, ct).ConfigureAwait(false);

                if (rows.Count <= size) return new TablePage(rows, null);
                rows.RemoveAt(size);
                return new TablePage(rows, rows[^1].Cursor);
            }

            var time = await TimeFieldAsync(ct).ConfigureAwait(false);
            var older = new List<FilterDefinition<BsonDocument>>(filters);
            if (query.Before is not null) older.Add(f.Lt(time, query.Before as BsonValue ?? throw Lost()));

            var page = await RowsAsync(All(older), projection, Builders<BsonDocument>.Sort.Descending(time),
                size + 1, window: true, ct).ConfigureAwait(false);

            if (page.Count <= size) return new TablePage(page, null);
            var next = page[size];
            page.RemoveAt(size);

            // No time, or one that can't be compared with, sorts last and can't be paged below.
            var last = page[^1].Time;
            if (Boundary(last) is not { } tie || next.Time is null) return new TablePage(page, null);

            // The next document is older: every one at this page's last time is on it already.
            if (!Equals(next.Time, last)) return new TablePage(page, tie);

            // Documents share the time, and "below this time" would skip the ones at it that missed this page: take them all
            // here, in the time index's order, so the server doesn't sort them.
            var ties = await RowsAsync(All([.. filters, f.Eq(time, tie)]), projection, Builders<BsonDocument>.Sort.Descending(time),
                LogTableRows.MaxTies + 1, window: true, ct).ConfigureAwait(false);

            var cut = ties.Count > LogTableRows.MaxTies;
            if (cut) ties.RemoveAt(LogTableRows.MaxTies);

            page.RemoveAll(r => Equals(r.Time, last));
            page.AddRange(ties);
            return new TablePage(page, tie, cut);
        }
        catch (MongoExecutionTimeoutException)
        {
            throw new LogTableException(LogTableRows.SlowPage, slow: true);
        }
        catch (TimeoutException ex)
        {
            throw new LogTableException(Describe(ex), slow: true);
        }
        catch (Exception ex) when (ex is not LogTableException and not OperationCanceledException and not OutOfMemoryException
                                       and not ObjectDisposedException)
        {
            throw new LogTableException(Describe(ex));
        }

        static LogTableException Lost() => new("That page can't be found any more. Go back to the newest page.");

        static FilterDefinition<BsonDocument> All(List<FilterDefinition<BsonDocument>> parts) =>
            parts.Count == 0 ? FilterDefinition<BsonDocument>.Empty : Builders<BsonDocument>.Filter.And(parts);
    }

    /// <summary>The time field, when <c>_id</c> doesn't count up — only if it is indexed: otherwise every read sorts the whole collection.</summary>
    private async Task<string> TimeFieldAsync(CancellationToken ct)
    {
        if (_map.Time is not { } time) throw new LogTableException("This collection's _id doesn't count up; pick a time field.");
        if (!await IndexedAsync(time, ct).ConfigureAwait(false))
            throw new LogTableException($"This collection's _id doesn't count up and \"{time}\" has no index: reading it would sort the whole collection on every poll.");
        return time;
    }

    /// <summary>
    /// The level names in the casings services write (MongoDB compares text exactly) and the numbers
    /// as ranges; a range only ever matches numbers, whatever their BSON type.
    /// </summary>
    internal static FilterDefinition<BsonDocument> LevelFilter(string field, LogSeverity floor)
    {
        var f = Builders<BsonDocument>.Filter;
        var any = new List<FilterDefinition<BsonDocument>> { f.In(field, LogParse.LevelNamesFrom(floor).Select(n => (BsonValue)n)) };

        foreach (var (low, high) in LogParse.LevelNumbersFrom(floor))
            any.Add(high is { } top ? f.And(f.Gte(field, low), f.Lte(field, top)) : f.Gte(field, low));

        return f.Or(any);
    }

    /// <summary>The term anywhere in a mapped text field, as written and ignoring case.</summary>
    internal static FilterDefinition<BsonDocument> SearchFilter(LogColumnMap map, string term)
    {
        var f = Builders<BsonDocument>.Filter;
        var fields = new[] { map.Message, map.Exception, map.Properties, map.Logger }
            .OfType<string>().Where(x => x != "_id").Distinct(StringComparer.Ordinal).ToList();

        // Nothing to search: a filter nothing passes.
        if (fields.Count == 0) return f.In("_id", Array.Empty<BsonValue>());

        var pattern = new BsonRegularExpression(Literal(term), "i");
        return f.Or(fields.Select(x => f.Regex(x, pattern)));
    }

    /// <summary>
    /// The term as a regular expression that matches only itself: every ASCII character that isn't a
    /// letter or a digit is escaped, which PCRE always reads as that character. Nothing else is touched,
    /// so a surrogate pair is never split.
    /// </summary>
    internal static string Literal(string term)
    {
        var text = new System.Text.StringBuilder(term.Length * 2);
        foreach (var c in term)
        {
            if (c < 128 && !char.IsAsciiLetterOrDigit(c)) text.Append('\\');
            text.Append(c);
        }

        return text.ToString();
    }

    /// <summary>An id cursor read back exactly — no overlap here: a page boundary is where the last page ended.</summary>
    private static BsonValue? Exact(object cursor)
    {
        if (cursor is not string text) return null;

        if (text.StartsWith("num:", StringComparison.Ordinal)
            && long.TryParse(text[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;

        return text.StartsWith("oid:", StringComparison.Ordinal) && ObjectId.TryParse(text[4..], out var oid) ? oid : null;
    }

    /// <summary>A row's time as the value to compare the field with. A BSON date is whole milliseconds, so it comes back exactly.</summary>
    private static BsonValue? Boundary(object? time) => time switch
    {
        DateTime t => new BsonDateTime(t),
        long n => n,
        double d => d,
        string s => s,
        _ => null,
    };

    /// <summary>
    /// Streamed, a batch at a time, each document cut to size as it arrives: a whole
    /// result held at once is a thousand documents of up to 16 MB each.
    /// </summary>
    private async Task<List<TableRow>> RowsAsync(FilterDefinition<BsonDocument> filter, ProjectionDefinition<BsonDocument> projection,
                                                 SortDefinition<BsonDocument> sort, int limit, bool window, CancellationToken ct)
    {
        var options = new FindOptions { MaxTime = TimeSpan.FromSeconds(MaxSeconds), BatchSize = 100 };
        var rows = new List<TableRow>(Math.Min(limit, 1000));

        using var cursor = await _collection.Find(filter, options).Project(projection).Sort(sort).Limit(limit)
            .ToCursorAsync(ct).ConfigureAwait(false);

        while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
            foreach (var doc in cursor.Current)
                rows.Add(Row(doc, window, rows.Count));

        return rows;
    }

    private async Task<bool> CountsUpAsync(CancellationToken ct)
    {
        var first = await _collection.Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { MaxTime = TimeSpan.FromSeconds(MaxSeconds) })
            .Project(Builders<BsonDocument>.Projection.Include("_id")).Sort(Builders<BsonDocument>.Sort.Descending("_id")).Limit(1)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // An empty collection: assume the usual ObjectIds, and look again once something arrives.
        if (first is null) return true;
        return first.GetValue("_id", BsonNull.Value).BsonType is BsonType.ObjectId or BsonType.Int32 or BsonType.Int64;
    }

    private async Task<bool> IndexedAsync(string field, CancellationToken ct)
    {
        using var indexes = await _collection.Indexes.ListAsync(ct).ConfigureAwait(false);
        foreach (var index in await indexes.ToListAsync(ct).ConfigureAwait(false))
        {
            if (index.TryGetValue("key", out var key) && key is BsonDocument keys && keys.ElementCount > 0
                && string.Equals(keys.GetElement(0).Name, field, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The mapped fields and <c>_id</c>. A field inside one already included is left out:
    /// MongoDB refuses "Properties" and "Properties.SourceContext" together (path collision).
    /// </summary>
    internal static ProjectionDefinition<BsonDocument> Projection(LogColumnMap map)
    {
        var projection = Builders<BsonDocument>.Projection.Include("_id");
        foreach (var field in Fields(map)) projection = projection.Include(field);
        return projection;
    }

    internal static IReadOnlyList<string> Fields(LogColumnMap map)
    {
        var fields = new[] { map.Time, map.Level, map.Message, map.Exception, map.Properties, map.Logger }
            .OfType<string>().Where(f => f != "_id").Distinct(StringComparer.Ordinal).ToList();

        return fields.Where(f => !fields.Any(other => other != f && f.StartsWith(other + ".", StringComparison.Ordinal))).ToList();
    }

    private static string? CursorOf(BsonValue id) => id.BsonType switch
    {
        BsonType.ObjectId => "oid:" + id.AsObjectId,
        BsonType.Int32 or BsonType.Int64 => "num:" + id.ToInt64().ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>Where the next read starts: the last number, or a few seconds before the last ObjectId.</summary>
    private static BsonValue? From(string cursor)
    {
        if (cursor.StartsWith("num:", StringComparison.Ordinal)
            && long.TryParse(cursor[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;

        if (!cursor.StartsWith("oid:", StringComparison.Ordinal) || !ObjectId.TryParse(cursor[4..], out var oid)) return null;

        // The smallest ObjectId of that second, less the overlap: timestamp first, the rest zero.
        var bytes = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(bytes, Math.Max(0, oid.Timestamp - OverlapSeconds));
        return new ObjectId(bytes);
    }

    private TableRow Row(BsonDocument doc, bool window, int index)
    {
        var id = doc.GetValue("_id", BsonNull.Value);
        long? number = id.BsonType is BsonType.Int32 or BsonType.Int64 ? id.ToInt64() : null;
        var key = id.IsBsonNull ? $"row{index}" : id.ToString()!;
        if (key.Length > KeyChars) key = key[..KeyChars];

        return new TableRow(
            Key: key,
            Cursor: window ? null : CursorOf(id),
            Number: number,
            Time: Value(Get(doc, _map.Time), 64),
            Level: Value(Get(doc, _map.Level), 64),
            Message: Value(Get(doc, _map.Message), LogTableRows.MessageChars) as string,
            Exception: Value(Get(doc, _map.Exception), LogTableRows.ExceptionChars) as string,
            Properties: Value(Get(doc, _map.Properties), LogTableRows.PropertiesChars) as string,
            Logger: Value(Get(doc, _map.Logger), LogTableRows.LoggerChars) as string);
    }

    private static BsonValue? Get(BsonDocument doc, string? path)
    {
        if (path is null) return null;

        BsonValue current = doc;
        foreach (var part in path.Split('.'))
        {
            if (current is not BsonDocument inner || !inner.TryGetValue(part, out current)) return null;
        }

        return current;
    }

    /// <summary>A .NET value the row mapping understands; text cut to <paramref name="max"/>.</summary>
    private static object? Value(BsonValue? value, int max)
    {
        if (value is null || value.IsBsonNull || value.BsonType == BsonType.Undefined) return null;

        try
        {
            object? plain = value.BsonType switch
            {
                BsonType.DateTime => value.ToUniversalTime(),
                BsonType.String => value.AsString,
                BsonType.Int32 or BsonType.Int64 => value.ToInt64(),
                BsonType.Double => value.AsDouble,
                BsonType.Boolean => value.AsBoolean.ToString(),
                _ => value.ToJson(Json),
            };

            return plain is string text && text.Length > max ? text[..max] : plain;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidCastException or FormatException)
        {
            // A date outside .NET's range, or a value the driver won't turn into text.
            return null;
        }
    }

    /// <summary>For the form: does it connect, which collections are there, what fields the chosen one has.</summary>
    public static async Task<TableProbe> ProbeAsync(string connection, LogSourceEntry draft, CancellationToken ct)
    {
        var host = HostOf(connection, draft.Database);
        MongoClient? client = null;

        try
        {
            var url = new MongoUrl(connection);
            var database = draft.Database ?? url.DatabaseName;
            if (string.IsNullOrWhiteSpace(database))
                return new TableProbe("Name the database: the connection string doesn't.", host, [], [], []);
            if (LogSourceRules.DatabaseProblem(database) is { } bad) return new TableProbe(bad, host, [], [], []);

            client = new MongoClient(Settings(url));
            var db = client.GetDatabase(database);

            // Only the collections this login may read; a find-only role can't list the rest
            // — and if it can't list at all, the collection typed in is still worth a look.
            var tables = new List<string>();
            string? problem = null;
            try
            {
                var options = new ListCollectionNamesOptions { AuthorizedCollections = true };
                var names = await (await db.ListCollectionNamesAsync(options, ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false);
                tables = names.Order(StringComparer.Ordinal).Take(1000).ToList();
            }
            catch (MongoCommandException ex)
            {
                problem = $"This login can't list the collections ({ex.CodeName}); type the collection's name.";
            }

            IReadOnlyList<TableColumn> columns = [];
            IReadOnlyList<LogEntry> preview = [];

            if (!string.IsNullOrWhiteSpace(draft.Table))
            {
                var found = new Dictionary<string, TableColumn>(StringComparer.Ordinal);
                using var sample = await db.GetCollection<BsonDocument>(draft.Table)
                    .Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { MaxTime = TimeSpan.FromSeconds(MaxSeconds), BatchSize = 5 })
                    .Sort(Builders<BsonDocument>.Sort.Descending("_id")).Limit(20)
                    .ToCursorAsync(ct).ConfigureAwait(false);

                // Names and types only, a batch at a time: the documents themselves can be large.
                while (await sample.MoveNextAsync(ct).ConfigureAwait(false))
                    foreach (var doc in sample.Current)
                        foreach (var element in doc.Elements)
                            if (found.Count < 200) found.TryAdd(element.Name, new TableColumn(element.Name, element.Value.BsonType.ToString()));

                columns = [.. found.Values];

                if (columns.Count > 0)
                {
                    try
                    {
                        var probeSource = new LogSourceEntry
                        {
                            Kind = LogSourceKinds.Mongo, Table = draft.Table, Database = database,
                            Columns = draft.Columns ?? LogTableRows.Guess(columns), Utc = draft.Utc,
                        };

                        await using var reader = new MongoLogReader(connection, probeSource);
                        var read = await reader.ReadAsync(null, 5, ct).ConfigureAwait(false);
                        preview = read.Rows.Select(r => LogTableRows.ToEntry(r, draft.Table, draft.Utc == true)).ToList();
                    }
                    catch (LogTableException ex)
                    {
                        problem = ex.Message;
                    }
                }
            }

            return new TableProbe(problem, host, tables, columns, preview);
        }
        catch (LogTableException ex)
        {
            return new TableProbe(ex.Message, host, [], [], []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return new TableProbe(Describe(ex), host, [], [], []);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        MongoAuthenticationException => "MongoDB didn't accept the sign-in.",
        MongoConfigurationException or ArgumentException or FormatException => "The connection string isn't valid.",
        MongoExecutionTimeoutException => $"The query took longer than {MaxSeconds} seconds.",
        TimeoutException => "MongoDB didn't answer within 10 seconds.",
        MongoCommandException command => $"MongoDB refused the request ({command.CodeName}).",
        MongoConnectionException => "Couldn't connect to MongoDB.",
        _ => "MongoDB couldn't be read.",
    };

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
