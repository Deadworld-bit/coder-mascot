using System.IO;

namespace CoderMascot.Core;

/// <summary>
/// Where git is, as an absolute path.
///
/// The same rule <see cref="CoderCli"/> exists for, and for the same reason.
/// With UseShellExecute=false a bare "git" goes to CreateProcess, which searches
/// the calling image's directory and the current directory *before* PATH. This
/// app ships as a portable folder people drop in Downloads, so a git.exe sitting
/// next to CoderMascot.exe would be run every time the Branches window opens —
/// with a Coder session token in %APPDATA% for it to read.
///
/// That hole was closed for the coder CLI and left open here, which is what a
/// second copy of a rule buys you. There is now one resolver per executable and
/// no caller anywhere passes a bare name.
/// </summary>
public static class GitCli
{
    private static string? _found;

    /// <summary>An explicit path from the config wins; otherwise a real search.</summary>
    public static string? Resolve(CoderConfig cfg)
    {
        var configured = cfg.GitPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Deliberately not cached: someone correcting a wrong path in the
            // config should not have to restart the app to be believed.
            return Path.IsPathFullyQualified(configured) && File.Exists(configured)
                ? configured
                : null;
        }

        if (_found is not null) return _found;

        foreach (var candidate in CandidatePaths())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return _found = candidate;
        }

        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var exe = OperatingSystem.IsWindows() ? "git.exe" : "git";

        if (OperatingSystem.IsWindows())
        {
            foreach (var v in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            {
                var root = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(root))
                {
                    yield return Path.Combine(root, "Git", "cmd", exe);
                    yield return Path.Combine(root, "Git", "bin", exe);
                }
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
                yield return Path.Combine(local, "Programs", "Git", "cmd", exe);
        }

        // Explicit PATH walk, so the app directory and the current directory are
        // never consulted.
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            string full;
            try { full = Path.Combine(dir.Trim('"'), exe); }
            catch (ArgumentException) { continue; }   // malformed PATH entry

            yield return full;
        }
    }
}
