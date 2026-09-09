namespace CoderMascot.Core;

/// <summary>A process holding a TCP port open, that looks like something you started.</summary>
public sealed record DevServer
{
    public required int Port { get; init; }
    public required int Pid { get; init; }

    /// <summary>Process name without the extension, e.g. "node", "dotnet".</summary>
    public required string Process { get; init; }

    /// <summary>How long the process has been up.</summary>
    public required TimeSpan Uptime { get; init; }

    public string Label => $"{Process} :{Port}";
}

/// <summary>
/// Which listening ports are a forgotten dev server, and which are the machine
/// doing its job.
///
/// This exists because the CPU/RAM watch structurally cannot see the thing it
/// was asked to see. A Vite server or a `dotnet watch` that you finished with
/// two hours ago sits at 0% CPU and a few hundred megabytes — it never comes
/// near <see cref="LoadWatch"/>'s thresholds — while still holding a port, a
/// file watcher and a chunk of the working set. It is the purest case of "left
/// running after I was done with it", and it was invisible.
///
/// Classification is by owning process rather than by port number, and that is
/// deliberate. Port ranges are a guess that gets both answers wrong: plenty of
/// real services live on 8080, and a dev server started on 4173 or 7291 looks
/// like nothing at all. The runtime that owns the socket is the honest signal —
/// nothing but a developer starts `node`, `uvicorn` or `dotnet` listening on a
/// desktop.
/// </summary>
public static class DevServers
{
    /// <summary>
    /// Runtimes a developer starts by hand. Deliberately not "anything unknown":
    /// a warning that fires on the machine's own services is one you turn off in
    /// a day, and half of what listens on a Windows box is Windows.
    /// </summary>
    private static readonly HashSet<string> Runtimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "deno", "bun", "npm", "pnpm", "yarn", "nodemon",
        "python", "python3", "pythonw", "uvicorn", "gunicorn", "flask", "hypercorn", "waitress",
        "dotnet", "func",
        "ruby", "rails", "puma", "php", "php-cgi",
        "java", "gradle", "mvn",
        "go", "air", "gin",
        "vite", "next", "webpack", "esbuild", "parcel", "serve", "http-server", "live-server",
        "ng", "vue-cli-service", "storybook",
        "caddy", "hugo", "jekyll",
    };

    /// <summary>
    /// Ports never worth reporting: the well-known range is the operating
    /// system's, and nothing a developer runs by hand needs to be there.
    /// </summary>
    public const int LowestInterestingPort = 1024;

    public static bool LooksLikeDevServer(string process, int port) =>
        port >= LowestInterestingPort && Runtimes.Contains(Strip(process));

    /// <summary>Trim the extension Windows reports, so "node.exe" matches "node".</summary>
    private static string Strip(string process)
    {
        var cut = process.LastIndexOf('.');
        return cut > 0 ? process[..cut] : process;
    }

    /// <summary>
    /// The ones old enough to be leftovers, newest excluded.
    ///
    /// The age gate is what stops this being useless: you are *supposed* to have
    /// a dev server running while you're working, and a warning that fires the
    /// moment you type `npm run dev` is a warning about doing your job.
    /// </summary>
    public static IReadOnlyList<DevServer> Stale(IEnumerable<DevServer> all, int afterMinutes)
    {
        if (afterMinutes <= 0) return [];

        var cutoff = TimeSpan.FromMinutes(afterMinutes);
        return [.. all.Where(s => s.Uptime >= cutoff)
                      .OrderByDescending(s => s.Uptime)];
    }

    /// <summary>"3 dev servers still listening — node :5173 up 6h".</summary>
    public static string Describe(IReadOnlyList<DevServer> stale)
    {
        if (stale.Count == 0) return string.Empty;

        var oldest = stale[0];
        var lead = stale.Count == 1
            ? "A dev server is still listening"
            : $"{stale.Count} dev servers are still listening";

        return $"{lead} — {oldest.Label} up {Friendly.Duration(oldest.Uptime.TotalSeconds)}.";
    }
}
