using System.IO;

namespace CoderMascot.Core;

/// <summary>
/// Where the coder CLI actually is, as an absolute path.
///
/// Never spawn the bare name "coder". With UseShellExecute=false that goes to
/// CreateProcess, whose search order checks the application directory and the
/// current directory *before* PATH — and cmd.exe does the same. This app ships
/// as a portable folder in a user-writable location, so a dropped coder.exe
/// beside the mascot would otherwise be run every 30 seconds with the session
/// token sitting in %APPDATA%, and again by the login button.
///
/// One resolver for every caller, because a second copy of this rule is a
/// second chance to skip it — which is exactly how the login button came to
/// hand the raw configured string to cmd.exe.
/// </summary>
public static class CoderCli
{
    private static string? _resolved;

    public static string? Resolve(CoderConfig cfg)
    {
        if (_resolved is not null) return _resolved;

        var configured = cfg.CoderCli;
        if (!string.IsNullOrWhiteSpace(configured) &&
            !string.Equals(configured, "coder", StringComparison.OrdinalIgnoreCase))
        {
            // An explicitly configured CLI must be an absolute path we can see.
            return _resolved = Path.IsPathFullyQualified(configured) && File.Exists(configured)
                ? configured
                : null;
        }

        foreach (var candidate in CandidatePaths())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return _resolved = candidate;
        }

        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var exe = OperatingSystem.IsWindows() ? "coder.exe" : "coder";

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", exe);

        foreach (var v in new[] { "ProgramFiles", "ProgramFiles(x86)" })
        {
            var root = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, "Coder", exe);
        }

        // Explicit PATH walk, so the app directory and CWD are never consulted.
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
