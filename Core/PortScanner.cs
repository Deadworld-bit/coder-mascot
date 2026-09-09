using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CoderMascot.Core;

/// <summary>
/// Which processes on this machine are listening on a TCP port.
///
/// Uses the IP Helper API rather than shelling out to netstat: netstat costs a
/// process spawn and a locale-dependent text parse, on a poll that runs for the
/// life of the app. GetExtendedTcpTable hands back the owning PID directly,
/// which is the only part that matters — a port with no name attached is not
/// something anyone can act on.
/// </summary>
public static class PortScanner
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;

    /// <summary>TCP_TABLE_OWNER_PID_LISTENER — listeners only, with owning PIDs.</summary>
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;      // big-endian in the low two bytes
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }

    /// <summary>
    /// Everything listening on this machine, tidied into one row per port and
    /// process.
    ///
    /// Both address families, because on Windows "localhost" resolves to ::1
    /// first and plenty of dev servers bind IPv6 only — an IPv4-only sweep would
    /// report nothing on the very port the browser is talking to, which is worse
    /// than having no port list at all.
    /// </summary>
    public static IReadOnlyList<Listener> ScanAll()
    {
        try
        {
            return LocalPorts.Combine(Listeners(), Describe);
        }
        catch (Exception ex)
        {
            // A port sweep is a nicety. It must never be the reason the mascot
            // stops reporting on the workspace.
            Debug.WriteLine($"[CoderMascot] port scan failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>Every dev-server-looking listener, oldest process first.</summary>
    public static IReadOnlyList<DevServer> Scan()
    {
        var found = new Dictionary<int, DevServer>();

        foreach (var listener in ScanAll())
        {
            if (!listener.Mine) continue;

            // One process usually listens more than once — IPv4 plus the HMR
            // socket plus a debugger port. Keep the lowest port per process,
            // which is nearly always the one you'd type into a browser, so a
            // single Vite server isn't reported as three.
            if (found.TryGetValue(listener.Pid, out var existing) && existing.Port <= listener.Port)
                continue;

            found[listener.Pid] = new DevServer
            {
                Port = listener.Port,
                Pid = listener.Pid,
                Process = listener.Process,
                Uptime = listener.Uptime,
            };
        }

        return [.. found.Values.OrderByDescending(s => s.Uptime)];
    }

    private static IEnumerable<RawListener> Listeners()
    {
        foreach (var row in Table(AF_INET)) yield return row;
        foreach (var row in Table(AF_INET6)) yield return row;
    }

    private static IEnumerable<RawListener> Table(int family)
    {
        var size = 0;
        var rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family,
            TCP_TABLE_OWNER_PID_LISTENER, 0);

        // A machine with IPv6 disabled answers this with an error rather than an
        // empty table; that is not a failure worth reporting.
        if (rc != ERROR_INSUFFICIENT_BUFFER && rc != 0) yield break;
        if (size <= sizeof(int)) yield break;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family,
                    TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                yield break;

            var rows = Marshal.ReadInt32(buffer);
            var rowSize = family == AF_INET
                ? Marshal.SizeOf<MIB_TCPROW_OWNER_PID>()
                : Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();

            // Guard against a row count that doesn't fit the buffer we were
            // given — this walks unmanaged memory by offset, so a bad count is
            // an out-of-bounds read rather than an exception.
            var capacity = (size - sizeof(int)) / rowSize;
            if (rows > capacity) rows = capacity;

            for (var i = 0; i < rows; i++)
            {
                var at = buffer + sizeof(int) + i * rowSize;

                if (family == AF_INET)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(at);
                    yield return new RawListener(
                        PortOf(row.localPort),
                        (int)row.owningPid,
                        Loopback: (row.localAddr & 0xFF) == 127,
                        AnyAddress: row.localAddr == 0);
                }
                else
                {
                    var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(at);
                    var addr = row.localAddr ?? [];

                    yield return new RawListener(
                        PortOf(row.localPort),
                        (int)row.owningPid,
                        Loopback: IsIPv6(addr, last: 1),
                        AnyAddress: IsIPv6(addr, last: 0));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>localPort is network byte order in the bottom two bytes.</summary>
    private static int PortOf(uint value) =>
        ((int)(value & 0xFF) << 8) + (int)((value >> 8) & 0xFF);

    /// <summary>::1 (last: 1) or :: (last: 0) — fifteen zero bytes and one known one.</summary>
    private static bool IsIPv6(byte[] addr, byte last)
    {
        if (addr.Length != 16) return false;

        for (var i = 0; i < 15; i++)
        {
            if (addr[i] != 0) return false;
        }

        return addr[15] == last;
    }

    private static (string Name, TimeSpan Uptime)? Describe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var up = DateTime.Now - p.StartTime;
            return (p.ProcessName, up < TimeSpan.Zero ? TimeSpan.Zero : up);
        }
        catch
        {
            // Exited between the table snapshot and here, or a protected system
            // process we may not open. Neither is something to report.
            return null;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);
}
