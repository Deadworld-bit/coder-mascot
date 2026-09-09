namespace CoderMascot.Core;

/// <summary>Who can reach a listening socket.</summary>
public enum PortScope
{
    /// <summary>127.0.0.1 or ::1 — this machine only.</summary>
    Loopback,

    /// <summary>0.0.0.0 or :: — anything that can route to this machine.</summary>
    AllInterfaces,

    /// <summary>Bound to one particular interface address.</summary>
    Specific,
}

/// <summary>One process holding one TCP port open.</summary>
public sealed record Listener
{
    public required int Port { get; init; }
    public required int Pid { get; init; }
    public required string Process { get; init; }
    public required TimeSpan Uptime { get; init; }
    public required PortScope Scope { get; init; }

    /// <summary>A runtime a developer starts by hand, rather than the machine's own.</summary>
    public bool Mine { get; init; }

    public string Url => $"http://localhost:{Port}";

    public string Where => Scope switch
    {
        PortScope.Loopback => "localhost only",
        PortScope.AllInterfaces => "all interfaces",
        _ => "one interface",
    };
}

/// <summary>What the OS handed back before it was tidied up.</summary>
public readonly record struct RawListener(int Port, int Pid, bool Loopback, bool AnyAddress);

/// <summary>
/// The listening ports on this machine, as a list you can read.
///
/// The tidying is the whole job here, and it is why this is separate from the
/// P/Invoke that produces the rows. A single dev server shows up in the OS table
/// three or four times — IPv4 and IPv6, sometimes a second bind — and a port
/// list that reports one Vite server four times is a port list nobody trusts
/// enough to use.
///
/// Merged per (port, process): the same port held by two different processes is
/// two genuinely different facts, and hiding one of them is how you spend an
/// afternoon wondering why a port you "freed" is still busy.
/// </summary>
public static class LocalPorts
{
    public static IReadOnlyList<Listener> Combine(
        IEnumerable<RawListener> rows,
        Func<int, (string Name, TimeSpan Uptime)?> describe)
    {
        var byKey = new Dictionary<(int Port, int Pid), (bool Loopback, bool Any)>();

        foreach (var row in rows)
        {
            if (row.Port is <= 0 or > 65535) continue;

            var key = (row.Port, row.Pid);
            if (byKey.TryGetValue(key, out var seen))
            {
                // Reachability is the widest of the binds, not the last one read:
                // a server on both 127.0.0.1 and 0.0.0.0 is on the network.
                byKey[key] = (seen.Loopback && row.Loopback, seen.Any || row.AnyAddress);
            }
            else
            {
                byKey[key] = (row.Loopback, row.AnyAddress);
            }
        }

        var listeners = new List<Listener>();

        foreach (var ((port, pid), scope) in byKey)
        {
            // No name means the process is gone or protected. A port with
            // nothing attached to it is not something anyone can act on.
            if (describe(pid) is not { } info) continue;

            listeners.Add(new Listener
            {
                Port = port,
                Pid = pid,
                Process = info.Name,
                Uptime = info.Uptime,
                Scope = scope.Any ? PortScope.AllInterfaces
                    : scope.Loopback ? PortScope.Loopback
                    : PortScope.Specific,
                Mine = DevServers.LooksLikeDevServer(info.Name, port),
            });
        }

        // By port number: this list is read by looking up a port, and sorting by
        // anything else turns that into a search.
        return [.. listeners.OrderBy(l => l.Port).ThenBy(l => l.Pid)];
    }

    /// <summary>
    /// The rows to show. Ports below 1024 are hidden unless asked for — on a
    /// Windows box that range is almost entirely the operating system, and it
    /// buries the four ports you actually started.
    /// </summary>
    public static IReadOnlyList<Listener> Visible(
        IEnumerable<Listener> all, string? query, bool includeSystem)
    {
        var rows = all.Where(l => includeSystem || l.Port >= DevServers.LowestInterestingPort);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();

            // A number is a port lookup — the question is "what is on 5173", and
            // matching that against a pid would answer a question nobody asked.
            rows = int.TryParse(needle, out var port)
                ? rows.Where(l => l.Port.ToString().StartsWith(needle, StringComparison.Ordinal)
                                  || l.Port == port)
                : rows.Where(l => l.Process.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        return [.. rows];
    }

    /// <summary>"14 ports listening · 3 yours" — the line under the list.</summary>
    public static string Summarise(IReadOnlyList<Listener> shown, int total)
    {
        if (total == 0) return "Nothing is listening.";

        var mine = shown.Count(l => l.Mine);
        var lead = $"{shown.Count} of {total} listening";

        return mine > 0 ? $"{lead} · {mine} look like yours" : lead;
    }

    /// <summary>Is anything holding this port right now?</summary>
    public static Listener? On(IEnumerable<Listener> all, int port) =>
        all.FirstOrDefault(l => l.Port == port);
}
