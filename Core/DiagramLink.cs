using System.Text.Json.Serialization;

namespace CoderMascot.Core;

public enum DiagramKind
{
    /// <summary>A file on this PC, opened by whatever the shell has for its extension.</summary>
    File,

    /// <summary>An https page, opened in the browser.</summary>
    Web,
}

/// <summary>Where a diagram opens from, once checked.</summary>
public sealed record DiagramPlace(string Target, DiagramKind Kind);

/// <summary>
/// A diagram filed under a watched repository: a draw.io file on this PC, or a
/// web page that shows one.
///
/// Only the link is kept. The app never reads the diagram, so a database schema
/// never ends up in its settings.
/// </summary>
public sealed class DiagramLink
{
    [JsonPropertyName("title")] public string? Title { get; set; }

    [JsonPropertyName("target")] public string? Target { get; set; }

    public const int MaxTitle = 60;

    /// <summary>Real links are a few hundred characters; anything near this carries a payload.</summary>
    public const int MaxUrl = 2048;

    /// <summary>Longest first, so "db.drawio.svg" is named "db" and not "db.drawio".</summary>
    public static readonly IReadOnlyList<string> Extensions = [".drawio.svg", ".drawio.png", ".drawio", ".dio"];

    private static readonly char[] Forbidden = ['"', '<', '>', '|', '?', '*'];

    /// <summary>What the chip says: the name given, or one read off the target.</summary>
    [JsonIgnore]
    public string Label => CleanTitle(Title) ?? (Inspect(Target).Place is { } place ? NameOf(place) : "diagram");

    public static string? CleanTitle(string? raw) => ProjectGroups.OneLine(raw, MaxTitle);

    /// <summary>
    /// The target as it will be opened, or why it can't be.
    ///
    /// What comes out goes to the shell with UseShellExecute, which runs
    /// whatever it is handed. So only an https page or a local file with a
    /// draw.io extension gets through: never a program, a folder, or a UNC
    /// share. A mapped drive letter is a share too, but one the user already
    /// signed in to.
    /// </summary>
    public static (DiagramPlace? Place, string? Problem) Inspect(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();

        // Explorer's "Copy as path" wraps the path in quotes.
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();

        if (text.Length == 0) return (null, "Fill in a draw.io file on this PC, or an https link.");

        if (text.Contains("://", StringComparison.Ordinal)) return InspectUrl(text);

        if (text.StartsWith('/') || text.StartsWith('~'))
            return (null, "That's a workspace path. Diagrams open on this PC, so give the copy here, like D:\\work\\db.drawio.");

        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
            return (null, "Network paths can't be linked. Copy the file to this PC, or link a page that shows it.");

        if (!(text.Length > 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/'))
            return (null, "Give the full path, starting with the drive, like D:\\work\\db.drawio, or an https link.");

        var path = text.Replace('/', '\\');
        while (path.Contains(@"\\", StringComparison.Ordinal)) path = path.Replace(@"\\", @"\");

        // A second colon names an alternate data stream (D:\run.exe:x.drawio),
        // which the extension check below would otherwise wave through.
        if (path.IndexOf(':', 2) >= 0 || path.IndexOfAny(Forbidden) >= 0 || path.Any(char.IsControl))
            return (null, "That path has characters a file name can't contain.");

        if (StripExtension(path) is null)
            return (null, "Only draw.io files can be linked: .drawio, .dio, .drawio.svg or .drawio.png.");

        return (new DiagramPlace(path, DiagramKind.File), null);
    }

    /// <summary>What decides two links are the same diagram. Windows paths ignore case; URLs don't.</summary>
    public static string Key(DiagramPlace place) =>
        place.Kind == DiagramKind.File ? place.Target.ToUpperInvariant() : place.Target;

    /// <summary>A name read off the target: the file name, the last part of the URL, or its host.</summary>
    public static string NameOf(DiagramPlace place)
    {
        if (place.Kind == DiagramKind.File)
        {
            var file = place.Target[(place.Target.LastIndexOf('\\') + 1)..];
            return CleanTitle(StripExtension(file)) ?? CleanTitle(file) ?? "diagram";
        }

        try
        {
            var uri = new Uri(place.Target);
            var last = uri.Segments.Select(s => s.Trim('/')).LastOrDefault(s => s.Length > 0);
            var name = last is null ? null : Uri.UnescapeDataString(last);

            return CleanTitle(StripExtension(name ?? string.Empty) ?? name) ?? uri.Host;
        }
        catch (Exception)
        {
            return "link";
        }
    }

    /// <summary>
    /// A project's list made fit to save: unusable targets dropped, one row per
    /// diagram, and null for none so the key stays out of the file.
    /// </summary>
    public static DiagramLink[]? TidyAll(IEnumerable<DiagramLink?>? links)
    {
        var kept = new List<DiagramLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var link in links ?? [])
        {
            if (link is null || Inspect(link.Target).Place is not { } place) continue;
            if (!seen.Add(Key(place))) continue;

            kept.Add(new DiagramLink { Title = CleanTitle(link.Title), Target = place.Target });
        }

        return kept.Count == 0 ? null : [.. kept];
    }

    private static (DiagramPlace? Place, string? Problem) InspectUrl(string text)
    {
        try
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
                return (null, "That link can't be read. Paste the whole address, starting with https://.");

            if (uri.Scheme != Uri.UriSchemeHttps)
                return (null, "Only https links can be saved.");

            // https://trusted@elsewhere reads as the first host and goes to the second.
            if (uri.UserInfo.Length > 0)
                return (null, "That link has a user name or password in it. Take it out and sign in through the browser.");

            // diagrams.net can pack the whole diagram into the link (#R…, ?create=).
            // Saved, that is the schema itself sitting in config.json.
            var absolute = uri.AbsoluteUri;
            if (absolute.Length > MaxUrl ||
                uri.Fragment.StartsWith("#R", StringComparison.Ordinal) ||
                (IsDrawIoHost(uri.Host) && uri.Query.Contains("create=", StringComparison.OrdinalIgnoreCase)))
                return (null, "That link carries the diagram itself. Save it as a file and link the file, or link the page it lives on.");

            return (new DiagramPlace(absolute, DiagramKind.Web), null);
        }
        catch (Exception)
        {
            // Uri accepts some hosts it then throws on when asked to print them.
            return (null, "That link can't be read. Paste the whole address, starting with https://.");
        }
    }

    private static bool IsDrawIoHost(string host) =>
        host.Equals("diagrams.net", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".diagrams.net", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("draw.io", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".draw.io", StringComparison.OrdinalIgnoreCase);

    /// <summary>The name without its draw.io extension, or null when it has none.</summary>
    private static string? StripExtension(string name)
    {
        foreach (var ext in Extensions)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];

        return null;
    }
}
