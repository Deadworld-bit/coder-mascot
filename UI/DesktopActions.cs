using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CoderMascot.UI;

/// <summary>
/// The things the mascot can actually do for you, rather than tell you about.
///
/// Every alert in this app used to end at "here is a problem"; the work of
/// resolving it was still yours, and the most common one — a Claude session
/// waiting for a yes — cost an alt-tab hunt through four identical-looking
/// terminals. These are the one-click versions.
/// </summary>
public static class DesktopActions
{
    private const int SW_RESTORE = 9;

    /// <summary>
    /// Bring the window for a project folder to the front.
    ///
    /// Matched on window title, because that is the only thing every candidate
    /// has in common: VS Code, Windows Terminal, JetBrains and a plain console
    /// all put the folder or the running command in their title, and none of
    /// them can be found from a session id that only exists inside the
    /// workspace. Best-effort by nature — returns false if nothing matched, so
    /// the caller can say so rather than appearing to do nothing.
    /// </summary>
    public static bool FocusWindowFor(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        var needle = folder.Trim();
        var self = Process.GetCurrentProcess().Id;
        var best = IntPtr.Zero;
        var bestScore = int.MinValue;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;

            var title = TitleOf(hwnd);
            if (title.Length == 0) return true;
            if (title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) return true;

            // Prefer an editor or terminal over anything else that happens to
            // have the project name in its title — a browser tab or a File
            // Explorer window is very unlikely to be where Claude is waiting.
            var score = ProcessName(pid) switch
            {
                "WindowsTerminal" or "wt" => 3,
                "Code" or "code" or "Cursor" or "devenv" or "rider64" or "idea64" => 2,
                "powershell" or "pwsh" or "cmd" or "conhost" or "alacritty" or "wezterm" => 2,
                "explorer" or "chrome" or "msedge" or "firefox" => -1,
                _ => 0,
            };

            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        if (best == IntPtr.Zero) return false;

        try
        {
            if (IsIconic(best)) ShowWindow(best, SW_RESTORE);

            if (SetForegroundWindow(best)) return true;

            // Windows refuses foreground changes from a process that doesn't own
            // the current foreground window, which is most of the time for a
            // WS_EX_NOACTIVATE ornament. Flashing the taskbar button is the
            // sanctioned fallback and still points you at the right window.
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = best,
                dwFlags = 0x3 | 0xC,     // FLASHW_ALL | FLASHW_TIMERNOFG
                uCount = 3,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CoderMascot] focus failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Open a console running `coder login &lt;url&gt;`, and leave it open so the
    /// user can read what it said.
    ///
    /// Two things this must get right, because it is a credential prompt.
    ///
    /// The CLI is resolved to an absolute path by <see cref="CoderCli"/> rather
    /// than passed by name: cmd.exe searches the current directory before PATH,
    /// so a bare "coder" here would undo the same protection the session watch
    /// takes care to apply.
    ///
    /// The tail is quoted by hand. ProcessStartInfo.ArgumentList quotes an
    /// argument only if it contains whitespace or a quote — it leaves &amp;, |, ^
    /// and friends alone, and cmd /k re-parses the whole tail as a command line.
    /// Inside double quotes those characters are inert, so the quoting is the
    /// protection; the URL check below is the belt to its braces.
    /// </summary>
    public static string? CoderLogin(string cliPath, string url)
    {
        if (!Path.IsPathFullyQualified(cliPath) || !File.Exists(cliPath))
            return "Could not find the coder CLI. Set \"coderCli\" in the config to its full path.";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            url.Contains('"'))
            return "Refusing to use that Coder URL.";

        // System32 by absolute path, for the same reason the CLI above is
        // resolved: a cmd.exe beside a portable copy of this app would otherwise
        // be the one that runs, inside a window the user is about to type a
        // credential into.
        var shell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        if (!File.Exists(shell)) return "Couldn't find the Windows command prompt.";

        try
        {
            var psi = new ProcessStartInfo(shell) { UseShellExecute = false };
            psi.ArgumentList.Add("/k");
            psi.ArgumentList.Add($"\"{cliPath}\" login \"{uri.AbsoluteUri}\"");

            Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Open http://localhost:&lt;port&gt; in the default browser.
    ///
    /// The URL is built here from an integer rather than taken as a string, so
    /// there is nothing for a caller to get wrong: UseShellExecute means "ask
    /// the shell what this is", and the shell will happily run things that are
    /// not web pages.
    /// </summary>
    public static string? OpenLocalPort(int port)
    {
        if (port is <= 0 or > 65535) return $"{port} is not a port.";

        try
        {
            var url = new UriBuilder(Uri.UriSchemeHttp, "localhost", port).Uri;
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not open localhost:{port} — {ex.Message}";
        }
    }

    private static string TitleOf(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;

        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);
}
