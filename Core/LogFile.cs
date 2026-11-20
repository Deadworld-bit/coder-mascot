using System.IO;
using System.IO.Enumeration;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace CoderMascot.Core;

/// <summary>
/// Reads log files within fixed bounds. A pasted log can be hundreds of megabytes,
/// one line can be a base64 blob, and the mascot is a desktop toy that must not
/// take a gigabyte to show the last hour of errors.
/// </summary>
public static class LogFile
{
    /// <summary>Only the end of a longer file is read.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;
    public const int MaxEntries = 10_000;
    public const int MaxLineChars = 64 * 1024;

    /// <summary>Files listed per folder, newest first; the rest are named but not read.</summary>
    public const int MaxFiles = 30;

    /// <summary>Entries per folder. Newer files fill it first; older files are listed, not read.</summary>
    public const int MaxFolderEntries = 50_000;

    /// <summary>
    /// Bytes read per folder, beside the entry budget: thirty files of a few huge
    /// entries each would pass the entry count and still hold a gigabyte of text.
    /// </summary>
    public const long MaxFolderBytes = 64L * 1024 * 1024;

    // Characters held, which is what memory is actually made of: templates and
    // property listings can make an entry bigger than the bytes it came from.
    public const long MaxFileChars = 16L * 1024 * 1024;
    public const long MaxFolderChars = 32L * 1024 * 1024;
    public const long MaxTotalChars = 96L * 1024 * 1024;

    /// <summary>Subfolder levels searched, so a pasted Logs\logInfor\ tree is found.</summary>
    public const int MaxDepth = 3;

    private static readonly Regex NameDate = new(
        @"(?<![0-9])(?<y>20[0-9]{2})[-_.]?(?<m>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12][0-9]|3[01])(?![0-9])",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(100));

    private static readonly Encoding Utf8 = new UTF8Encoding(false, false);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(false, false, false);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(true, false, false);

    /// <param name="name">What the entries call their source: the path relative to the folder.</param>
    public static LogFileRead Read(string path, string name, long maxBytes = MaxBytes, int maxEntries = MaxEntries,
                                   long maxChars = MaxFileChars)
    {
        maxEntries = Math.Max(1, maxEntries);
        long length = 0;
        var written = DateTime.MinValue;

        try
        {
            written = File.GetLastWriteTimeUtc(path);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

            length = stream.Length;
            var (encoding, bom) = Sniff(stream);

            long start = bom, skipped = 0;
            if (length - bom > maxBytes)
            {
                start = length - maxBytes;
                if (encoding is UnicodeEncoding && (start - bom) % 2 != 0) start++;
                skipped = start - bom;
            }

            stream.Position = start;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, 64 * 1024);

            var lines = Lines(reader, MaxLineChars);
            if (skipped > 0) lines = lines.Skip(1);   // starts part-way through a line

            var fallback = new DateTimeOffset(DateTime.SpecifyKind(written, DateTimeKind.Utc)).ToLocalTime();
            var kept = new Queue<LogEntry>();
            var dropped = 0;
            var held = 0L;

            foreach (var entry in LogParse.Entries(lines, name, DateIn(name), fallback, fromMiddle: skipped > 0))
            {
                kept.Enqueue(entry);
                held += Size(entry);

                while (kept.Count > maxEntries || (held > maxChars && kept.Count > 1))
                {
                    held -= Size(kept.Dequeue());
                    dropped++;
                }
            }

            return new LogFileRead(name, path, length, written, [.. kept], dropped, skipped, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new LogFileRead(name, path, length, written, [], 0, 0, Describe(ex));
        }
    }

    /// <summary>
    /// Matching files under <paramref name="folder"/>, newest first. Hidden and system
    /// files are skipped, and so are junctions and symlinks, which could lead the
    /// listing out of the folder or round in a loop.
    /// </summary>
    public static List<FileInfo> List(string folder, IReadOnlyList<string> patterns)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        return new DirectoryInfo(folder)
            .EnumerateFiles("*", options)
            .Where(f => patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, f.Name, ignoreCase: true)))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The date in a rolling file's name: log-20261023.txt, app_2026-10-23.log.</summary>
    public static DateOnly? DateIn(string name)
    {
        try
        {
            var m = NameDate.Match(Path.GetFileName(name));
            if (!m.Success) return null;

            var year = int.Parse(m.Groups["y"].ValueSpan);
            var month = int.Parse(m.Groups["m"].ValueSpan);
            var day = int.Parse(m.Groups["d"].ValueSpan);
            return day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The encoding, and how many BOM bytes to step over. Without a BOM, a run of zero
    /// bytes in every other position is UTF-16 (PowerShell 5's redirect, some Windows
    /// tools); anything else is read as UTF-8, with bad bytes shown as replacement marks.
    /// </summary>
    private static (Encoding Encoding, int Bom) Sniff(Stream stream)
    {
        var head = new byte[4096];
        var n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return (Utf8, 3);
        if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) return (Utf16Le, 2);
        if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) return (Utf16Be, 2);

        var pairs = n / 2;
        if (pairs >= 2)
        {
            int evenZeros = 0, oddZeros = 0;
            for (var i = 0; i + 1 < n; i += 2)
            {
                if (head[i] == 0) evenZeros++;
                if (head[i + 1] == 0) oddZeros++;
            }

            if (oddZeros > pairs * 0.3 && evenZeros < pairs * 0.05) return (Utf16Le, 0);
            if (evenZeros > pairs * 0.3 && oddZeros < pairs * 0.05) return (Utf16Be, 0);
        }

        return (Utf8, 0);
    }

    /// <summary>The characters an entry holds, for the character budgets.</summary>
    public static long Size(LogEntry entry) =>
        entry.FirstLine.Length + entry.Message.Length + (entry.Detail?.Length ?? 0L);

    /// <summary>Lines without their CR/LF, each cut at <paramref name="maxChars"/> and marked with "…" where it was.</summary>
    internal static IEnumerable<string> Lines(TextReader reader, int maxChars)
    {
        var buffer = new char[64 * 1024];
        var line = new StringBuilder();
        var open = false;
        var cut = false;
        int read;

        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            while (start < read)
            {
                var newline = Array.IndexOf(buffer, '\n', start, read - start);
                var end = newline < 0 ? read : newline;

                var room = maxChars - line.Length;
                var take = Math.Min(Math.Max(room, 0), end - start);
                if (take > 0) line.Append(buffer, start, take);
                if (take < end - start) cut = true;
                open = true;

                if (newline < 0) break;

                yield return Finish(line, cut);
                line.Clear();
                open = false;
                cut = false;
                start = newline + 1;
            }
        }

        if (open) yield return Finish(line, cut);
    }

    private static string Finish(StringBuilder line, bool cut)
    {
        var text = line.ToString();
        if (text.EndsWith('\r')) text = text[..^1];

        // Some loggers pre-allocate their file with zero bytes.
        if (text.Contains('\0')) text = text.Replace("\0", "");

        if (!cut) return text;

        // A cut between the halves of a surrogate pair leaves half a character,
        // which the JSON reader refuses outright.
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
        return text + "…";
    }

    private static string Describe(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "The file has gone.",
        UnauthorizedAccessException or SecurityException => "Windows won't let the mascot read it.",
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "Another program has it open and isn't sharing it.",
        _ => "It couldn't be read: " + ex.Message,
    };
}
