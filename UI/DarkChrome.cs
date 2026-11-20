using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CoderMascot.UI;

/// <summary>
/// A dark native title bar, so a dark window doesn't sit under a white strip.
///
/// Windows 10 20H1 and later read attribute 20; 1809 to 1909 read 19 for the
/// same thing. Anything older ignores both and keeps the light bar.
/// </summary>
public static class DarkChrome
{
    private const int DarkMode = 20;
    private const int DarkModeBefore20H1 = 19;

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) Set(handle);
        else window.SourceInitialized += (_, _) => Set(new WindowInteropHelper(window).Handle);
    }

    private static void Set(IntPtr hwnd)
    {
        var on = 1;
        if (DwmSetWindowAttribute(hwnd, DarkMode, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DarkModeBefore20H1, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
