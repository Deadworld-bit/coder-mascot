using System.Security.Cryptography;
using System.Text;

namespace CoderMascot.Core;

/// <summary>A URL after it has been looked at: usable, or refused with a reason.</summary>
public readonly record struct GitUrlCheck(string? Url, string? Problem, bool StrippedSignIn);

/// <summary>
/// Deciding whether a string may be handed to git as a remote.
///
/// This is the one piece of user input in the app that becomes an argument to a
/// program that will *connect somewhere* with it, so the rule is an allowlist,
/// not a blocklist. Two of the things git accepts here are genuinely dangerous:
///
///   ext::sh -c whatever     git's transport-helper syntax runs a command
///   -u/--upload-pack=…      a leading dash is an option wherever it lands
///
/// and two more are merely wrong in a way that fails confusingly later: a
/// Windows path (C:\src\app) parses as scp-style "host C, path \src\app", and a
/// UNC path is not a URL at all.
///
/// Credentials embedded in the URL are removed rather than stored. There is
/// nowhere in this app that a password belongs in cleartext — the same rule that
/// keeps the Coder CLI's session token out of config.json — and Git's own
/// credential manager already handles signing in to a private server.
/// </summary>
public static class GitUrl
{
    public static string? Clean(string? raw) => Inspect(raw).Url;

    public static GitUrlCheck Inspect(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();

        if (text.Length == 0) return new(null, "Paste the repository's URL.", false);

        // Wherever this string ends up on a command line, a leading dash is read
        // as an option rather than an address.
        if (text.StartsWith('-')) return new(null, "A repository URL can't start with \u201c-\u201d.", false);

        if (text.Any(c => char.IsControl(c) || c == ' '))
            return new(null, "That URL has a space or a line break in it.", false);

        // <helper>::<address> is how git invokes a transport helper, and
        // ext::sh -c … runs a shell command. Never, under any scheme.
        if (text.Contains("::", StringComparison.Ordinal))
            return new(null, "That isn't a repository URL \u2014 git reads \u201c::\u201d as a command to run.", false);

        if (text.Contains('\\'))
            return new(null, "That looks like a Windows path. Use \u201con this PC\u201d for a folder, "
                             + "or paste the https:// address of the repository.", false);

        return text.Contains("://", StringComparison.Ordinal) ? WithScheme(text) : ScpStyle(text);
    }

    private static GitUrlCheck WithScheme(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
            return new(null, "That isn't a URL git can use.", false);

        var scheme = parsed.Scheme.ToLowerInvariant();

        if (scheme is "http" && !parsed.IsLoopback)
            return new(null, "http:// sends your sign-in across the network in the clear. "
                             + "Use the https:// address of the same repository.", false);

        if (scheme is not ("https" or "http" or "ssh"))
            return new(null, $"{scheme}:// isn't a transport this reads from. Use https:// or ssh://.", false);

        var signIn = !string.IsNullOrEmpty(parsed.UserInfo);
        if (!signIn) return new(text, null, false);

        // The username is kept: it is not a secret, it is part of how both ssh
        // and some https remotes are written, and silently rewriting somebody's
        // URL is its own small betrayal. The password after the colon is the
        // secret, and that is what goes.
        var user = parsed.UserInfo.Split(':')[0];
        var keep = user.Length > 0 ? user + "@" : string.Empty;

        var port = parsed.IsDefaultPort ? string.Empty : $":{parsed.Port}";
        var rebuilt = $"{scheme}://{keep}{parsed.Host}{port}{parsed.PathAndQuery}";

        return new(rebuilt.TrimEnd('?'), null, parsed.UserInfo.Contains(':'));
    }

    /// <summary>git@host:group/repo.git — no scheme, and the colon comes before any slash.</summary>
    private static GitUrlCheck ScpStyle(string text)
    {
        var colon = text.IndexOf(':');
        var slash = text.IndexOf('/');

        if (colon <= 0 || (slash >= 0 && slash < colon))
            return new(null, "That isn't a repository URL. It should look like "
                             + "https://git.example.com/team/app.git.", false);

        var host = text[..colon];
        var path = text[(colon + 1)..];

        // "C:/src/app" reaches here as host "C". A one-letter host is a drive.
        if (host.TrimEnd('@').Length < 2 || host.Split('@')[^1].Length < 2)
            return new(null, "That looks like a path on this PC. Use \u201con this PC\u201d for a folder, "
                             + "or paste the https:// address of the repository.", false);

        if (path.Length == 0)
            return new(null, "That URL has a host but no repository after the colon.", false);

        // git reads the first colon as the host separator, so "user:pw@host:path"
        // does not mean what whoever typed it thinks — git would look for a host
        // called "user". Left alone it also lands a password in the path and
        // straight into config.json, which is the one thing this must not do.
        if (path.Contains('@'))
            return new(null, "That isn't a form git can use \u2014 there's an \u201c@\u201d after the colon. "
                             + "Use the https:// address, and let Git's credential manager sign you in.", false);

        var at = host.IndexOf('@');
        if (at < 0) return new(text, null, false);

        // user@host is normal here; user:password@host is not.
        var user = host[..at];
        return user.Contains(':')
            ? new($"{user.Split(':')[0]}@{host[(at + 1)..]}:{path}", null, true)
            : new(text, null, false);
    }

    /// <summary>The repository's own name, for the sidebar and the folder on disk.</summary>
    public static string Slug(string url)
    {
        var text = (url ?? string.Empty).Trim().TrimEnd('/');

        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text[..^4];

        var last = text.Split('/', ':').LastOrDefault(part => part.Length > 0) ?? string.Empty;

        var clean = new string([.. last.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-')])
            .Trim('-', '.');

        if (clean.Length == 0) clean = "repository";

        return clean.Length > 40 ? clean[..40].TrimEnd('-', '.') : clean;
    }

    /// <summary>
    /// The folder this repository's copy lives in: readable, and unique.
    ///
    /// The name alone is not enough — two servers hosting a repository called
    /// "api" would share one folder and each fetch would fight the other — so it
    /// carries a short digest of the whole URL. Derived, never stored, so it
    /// stays correct if the naming ever changes.
    /// </summary>
    public static string Folder(string url)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(url ?? string.Empty)))[..8].ToLowerInvariant();

        return $"{Slug(url ?? string.Empty)}-{digest}";
    }
}
