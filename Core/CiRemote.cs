using System.Text.RegularExpressions;

namespace CoderMascot.Core;

/// <summary>
/// Where a repository's pipelines live: turning the one thing most people give
/// this — a git remote — into a provider, an API base, and a project path.
///
/// The remote is the only input we can count on, so every spelling git accepts
/// has to land on the same server and project, and the one host whose API lives
/// somewhere else (github.com → api.github.com) has to be recognised. A CI server
/// is only ever spoken to over https: a token in a header down a cleartext http
/// connection is the whole credential, in the clear, so http is refused outright.
/// </summary>
public static partial class CiRemote
{
    /// <summary>A git remote → (host, "group/project"), or null if it isn't one.</summary>
    public static (string Host, string Project)? Parse(string? remote)
    {
        var s = remote?.Trim();
        if (string.IsNullOrEmpty(s)) return null;

        string host, path;

        if (s.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else if (ScpStyle().Match(s) is { Success: true } m)
        {
            // git@host:group/project — and nothing without the '@', so a Windows
            // path like C:\src\app (colon, no '@') is not mistaken for a remote.
            host = m.Groups[1].Value;
            path = m.Groups[2].Value;
        }
        else
        {
            return null;
        }

        var project = path.Trim().Trim('/');
        if (project.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) project = project[..^4];
        project = project.Trim('/');

        // A bare host is not a project, and a project is always group/name.
        if (host.Length == 0 || !project.Contains('/')) return null;

        return (host, project);
    }

    /// <summary>
    /// Resolve where to read a repository's pipelines from, or null when there is
    /// nowhere to ask (CI off, no remote yet, or a server we may not use).
    /// </summary>
    public static CiTarget? Resolve(GitProject project, string? remote)
    {
        if (project.Ci == CiKind.Off) return null;

        string host;
        string? projectPath;

        if (!string.IsNullOrWhiteSpace(project.CiUrl))
        {
            // A server named in the config. http is refused; a bare host is made https.
            if (CleanServer(project.CiUrl) is not { } site) return null;
            host = new Uri(site).Host;
            projectPath = Clean(project.CiProject) ?? ReadRemote(project, remote)?.Project;
        }
        else
        {
            if (ReadRemote(project, remote) is not { } found) return null;
            host = found.Host;
            projectPath = Clean(project.CiProject) ?? found.Project;   // an explicit path wins over the URL's
        }

        if (host.Length == 0 || string.IsNullOrEmpty(projectPath) || !IsCleanProject(projectPath)) return null;

        var provider = ProviderFor(project.Ci, host);
        var (apiBase, site2) = Endpoints(provider, host);
        return new CiTarget(provider, apiBase, site2, projectPath);
    }

    /// <summary>How the editor names a resolved target: "GitLab at host · group/project".</summary>
    public static string Describe(CiTarget target)
    {
        var name = target.Provider == CiProvider.GitHub ? "GitHub" : "GitLab";
        var host = Uri.TryCreate(target.Site, UriKind.Absolute, out var u) ? u.Host : target.Site;
        return $"{name} at {host} · {target.Project}";
    }

    /// <summary>
    /// A "group/project" path safe to build a request URL from: two or more
    /// segments, each of ordinary name characters, none empty or "."/"..".
    ///
    /// The gate that keeps a hostile remote or config out of the credentialed
    /// request — a "?", "#" or ".." in the path could otherwise redirect the call
    /// or traverse to another endpoint. Anything that fails this just means "no
    /// CI for that repository", which is the safe way to be wrong.
    /// </summary>
    private static bool IsCleanProject(string project)
    {
        var segments = project.Split('/');
        if (segments.Length < 2) return false;

        foreach (var s in segments)
        {
            if (s.Length == 0 || s is "." or "..") return false;
            foreach (var c in s)
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) return false;
        }

        return true;
    }

    /// <summary>A URL remote from its Path (RepoHost.Remote), else the passed-in remote.</summary>
    private static (string Host, string Project)? ReadRemote(GitProject project, string? remote) =>
        project.Host == RepoHost.Remote ? Parse(project.Path) : (remote is null ? null : Parse(remote));

    private static CiProvider ProviderFor(CiKind ci, string host) => ci switch
    {
        CiKind.GitHub => CiProvider.GitHub,
        CiKind.GitLab => CiProvider.GitLab,
        _ => host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? CiProvider.GitHub : CiProvider.GitLab,
    };

    private static (string ApiBase, string Site) Endpoints(CiProvider provider, string host)
    {
        if (provider == CiProvider.GitHub)
        {
            return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? ("https://api.github.com", "https://github.com")
                : ($"https://{host}/api/v3", $"https://{host}");   // GitHub Enterprise
        }

        return ($"https://{host}", $"https://{host}");
    }

    /// <summary>
    /// A configured CI server → "https://host", or null when it can't be used.
    ///
    /// https only: a token in a header down cleartext http is the whole
    /// credential exposed. A bare host is made https. A value git would read as a
    /// remote-helper command ("ext::sh -c id", "fd::…") is refused — this string
    /// becomes an address something connects to, so it must not smuggle a command.
    /// </summary>
    public static string? CleanServer(string? value)
    {
        var t = value?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(t)) return null;
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;

        // "ext::", "fd::" and friends carry a "::" but no "://".
        if (t.Contains("::", StringComparison.Ordinal) && !t.Contains("://", StringComparison.Ordinal)) return null;

        if (!t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;

        if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        return u.Host.Length == 0 ? null : $"https://{u.Host}";
    }

    private static string? Clean(string? project)
    {
        var t = project?.Trim().Trim('/');
        return string.IsNullOrEmpty(t) ? null : t;
    }

    [GeneratedRegex(@"^[^/@]+@([^:/]+):(.+)$")]
    private static partial Regex ScpStyle();
}
