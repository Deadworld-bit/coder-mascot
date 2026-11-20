using System.Text.Json.Serialization;

namespace CoderMascot.Core;

public static class LogSourceKinds
{
    public const string Folder = "folder";
    public const string SqlServer = "sqlserver";
    public const string Mongo = "mongo";

    /// <summary>A database table or collection, read by connection string.</summary>
    public static bool IsTable(string? kind) => kind is SqlServer or Mongo;
}

/// <summary>Which column (or MongoDB field) plays which part of an entry. Null leaves a part out.</summary>
public sealed class LogColumnMap
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("time")] public string? Time { get; set; }
    [JsonPropertyName("level")] public string? Level { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("exception")] public string? Exception { get; set; }
    [JsonPropertyName("properties")] public string? Properties { get; set; }
    [JsonPropertyName("logger")] public string? Logger { get; set; }

    public bool SameAs(LogColumnMap? other) =>
        other is not null
        && Same(Id, other.Id) && Same(Time, other.Time) && Same(Level, other.Level) && Same(Message, other.Message)
        && Same(Exception, other.Exception) && Same(Properties, other.Properties) && Same(Logger, other.Logger);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
}

/// <summary>One place the Logs page reads from, as the config file holds it.</summary>
public sealed class LogSourceEntry
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("kind")] public string Kind { get; set; } = LogSourceKinds.Folder;

    [JsonPropertyName("path")] public string? Path { get; set; }

    /// <summary>File-name patterns, separated by semicolons.</summary>
    [JsonPropertyName("files")] public string? Files { get; set; }

    /// <summary>A table's connection string, sealed for this Windows account (<see cref="LogSecrets"/>). Never shown again.</summary>
    [JsonPropertyName("connection")] public string? Connection { get; set; }

    /// <summary>Server and database, for display. Taken from the connection string when it was saved; no credentials.</summary>
    [JsonPropertyName("host")] public string? Host { get; set; }

    /// <summary>MongoDB's database, when the connection string doesn't name it.</summary>
    [JsonPropertyName("database")] public string? Database { get; set; }

    /// <summary>The table (schema.table) or the MongoDB collection.</summary>
    [JsonPropertyName("table")] public string? Table { get; set; }

    [JsonPropertyName("columns")] public LogColumnMap? Columns { get; set; }

    /// <summary>The time column holds UTC without saying so. Null means no.</summary>
    [JsonPropertyName("utc")] public bool? Utc { get; set; }

    [JsonIgnore]
    public string Id => LogSourceKinds.IsTable(Kind) ? $"{Kind}:{Host}/{Database}/{Table}" : Path ?? "";

    [JsonIgnore] public IReadOnlyList<string> Patterns => LogSourceRules.Patterns(Files);

    public bool SameAs(LogSourceEntry other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal) && ReadsSameAs(other);

    /// <summary>Everything that decides what is read — all of it but the name.</summary>
    public bool ReadsSameAs(LogSourceEntry other) =>
        string.Equals(Kind, other.Kind, StringComparison.Ordinal)
        && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Files, other.Files, StringComparison.Ordinal)
        && string.Equals(Connection, other.Connection, StringComparison.Ordinal)
        && string.Equals(Host, other.Host, StringComparison.Ordinal)
        && string.Equals(Database, other.Database, StringComparison.Ordinal)
        && string.Equals(Table, other.Table, StringComparison.Ordinal)
        && (Columns is null ? other.Columns is null : Columns.SameAs(other.Columns))
        && (Utc ?? false) == (other.Utc ?? false);
}

public readonly record struct LogFolderCheck(string? Folder, string? Problem);

/// <summary>What the config file and the source form both accept.</summary>
public static class LogSourceRules
{
    public const int MaxSources = 20;
    public const int MaxPatterns = 8;
    public const int MaxNameChars = 60;
    public const string DefaultFiles = "*.txt;*.log;*.json;*.clef";

    private const int MaxConnectionChars = 8192;
    private const int MaxTableChars = 256;
    private const int MaxColumnChars = 128;
    private const int MaxHostChars = 200;

    /// <summary>
    /// The sources, made usable: a folder that passes <see cref="Inspect"/>, or a
    /// table with a connection string and a table name; each once; a name always set.
    /// One definition for the loader and the form, like the project list's.
    /// </summary>
    public static LogSourceEntry[] Normalize(IEnumerable<LogSourceEntry?>? entries)
    {
        var kept = new List<LogSourceEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries ?? [])
        {
            var clean = entry is null ? null : Clean(entry);
            if (clean is null || !seen.Add(clean.Id)) continue;

            kept.Add(clean);
            if (kept.Count == MaxSources) break;
        }

        return [.. kept];
    }

    private static LogSourceEntry? Clean(LogSourceEntry entry)
    {
        var kind = entry.Kind?.Trim().ToLowerInvariant();

        if (kind == LogSourceKinds.Folder)
        {
            if (Inspect(entry.Path).Folder is not { } folder) return null;

            return new LogSourceEntry
            {
                Name = CleanName(entry.Name, folder[(folder.LastIndexOf('\\') + 1)..]),
                Kind = LogSourceKinds.Folder,
                Path = folder,
                Files = CleanFiles(entry.Files),
            };
        }

        if (!LogSourceKinds.IsTable(kind)) return null;

        var connection = entry.Connection?.Trim();
        var table = Text(entry.Table, MaxTableChars);
        if (string.IsNullOrEmpty(connection) || connection.Length > MaxConnectionChars || table is null) return null;

        var database = Text(entry.Database, MaxColumnChars);
        if (database is not null && DatabaseProblem(database) is not null) return null;

        return new LogSourceEntry
        {
            Name = CleanName(entry.Name, table[(table.LastIndexOf('.') + 1)..].Trim('[', ']')),
            Kind = kind!,
            Connection = connection,
            Host = CleanHost(entry.Host),
            Database = database,
            Table = table,
            Columns = CleanColumns(entry.Columns),
            Utc = entry.Utc == true ? true : null,
        };
    }

    private static LogColumnMap? CleanColumns(LogColumnMap? map)
    {
        if (map is null) return null;

        var clean = new LogColumnMap
        {
            Id = Text(map.Id, MaxColumnChars),
            Time = Text(map.Time, MaxColumnChars),
            Level = Text(map.Level, MaxColumnChars),
            Message = Text(map.Message, MaxColumnChars),
            Exception = Text(map.Exception, MaxColumnChars),
            Properties = Text(map.Properties, MaxColumnChars),
            Logger = Text(map.Logger, MaxColumnChars),
        };

        return clean.SameAs(new LogColumnMap()) ? null : clean;
    }

    /// <summary>Why MongoDB would refuse this database name, or null when it wouldn't.</summary>
    public static string? DatabaseProblem(string name) =>
        name.IndexOfAny(['.', '$', '/', '\\', '"', ' ', '\0']) >= 0 || name.Length > 63
            ? "A MongoDB database name can't contain . $ / \\ \" or spaces."
            : null;

    // A long seed list is still a label: cut, not thrown away, so the source's Id stays put.
    private static string CleanHost(string? raw)
    {
        var host = new string((raw ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (host.Length == 0) return "server";
        return host.Length > MaxHostChars ? host[..MaxHostChars] : host;
    }

    /// <summary>Trimmed, without control characters, within <paramref name="max"/>; null when nothing is left or it is too long.</summary>
    private static string? Text(string? raw, int max)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > max || text.Any(char.IsControl)) return null;
        return text;
    }

    /// <summary>
    /// A folder the mascot will list every few seconds, or why not.
    ///
    /// A drive-letter path only. A UNC share that has gone offline holds each
    /// listing for the SMB timeout, and a drive root is a recursive walk of the
    /// whole disk on every poll.
    /// </summary>
    public static LogFolderCheck Inspect(string? raw)
    {
        var path = raw?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(path)) return new(null, "Pick a folder.");
        if (path.Length > 1024) return new(null, "That path is too long.");
        if (path.Any(char.IsControl)) return new(null, "That path has control characters in it.");

        if (path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal))
            return new(null, "That's a workspace path. The mascot runs on Windows and reads Windows folders.");

        path = path.Replace('/', '\\');

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return new(null, "Network shares aren't supported. Copy the files to a folder on this PC.");

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            return new(null, @"Use a full path that starts with a drive letter, like D:\Logs\eContract.");

        // Explorer splits its argument at commas, so Open folder would go somewhere else.
        if (path.IndexOfAny(['*', '?', '"', '<', '>', '|', ',']) >= 0 || path.IndexOf(':', 2) >= 0)
            return new(null, "A folder path can't contain * ? \" < > | , or a second colon.");

        var parts = path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new(null, "Pick a folder, not a whole drive.");
        if (parts.Any(p => p.Trim().Trim('.').Length == 0))
            return new(null, @"A folder path can't contain . or .. steps.");

        return new(char.ToUpperInvariant(path[0]) + @":\" + string.Join('\\', parts), null);
    }

    /// <summary>The patterns, cleaned: no path characters, no blanks or repeats, the default when empty.</summary>
    public static string CleanFiles(string? raw)
    {
        var patterns = (raw ?? "")
            .Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length <= 64 && !p.Any(c => char.IsControl(c) || c is '\\' or '/' or ':' or '"' or '<' or '>' or '|'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPatterns)
            .ToArray();

        return patterns.Length == 0 ? DefaultFiles : string.Join(';', patterns);
    }

    public static IReadOnlyList<string> Patterns(string? files) =>
        CleanFiles(files).Split(';');

    private static string CleanName(string? raw, string fallback)
    {
        var name = new string((raw ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = fallback;
        return name.Length > MaxNameChars ? name[..MaxNameChars] : name;
    }
}
