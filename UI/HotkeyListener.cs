using System.Runtime.InteropServices;
using System.Windows.Interop;
using CoderMascot.Core;

namespace CoderMascot.UI;

/// <summary>
/// A system-wide shortcut for "new note", so capturing a thought never costs
/// you the window you were looking at.
///
/// Registered against a message-only window: this app has no main window, and
/// hanging the hook on the mascot would tie the shortcut to a window the user
/// is allowed to hide.
///
/// Failure here is ordinary, not exceptional — another application may already
/// own the combination, and Windows simply refuses. That is reported as a
/// sentence the user can act on, never as a crash and never as silence.
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xC0DE;

    private HwndSource? _sink;
    private bool _registered;

    public event EventHandler? Pressed;

    /// <summary>What is actually bound, once it is bound.</summary>
    public string? Bound { get; private set; }

    /// <summary>Bind it. Returns null, or why it could not be.</summary>
    public string? Register(string? text)
    {
        Unregister();

        if (Hotkey.Parse(text) is not { } spec)
            return $"\"{text}\" isn't a shortcut this understands — try something like {Hotkey.Default}.";

        _sink = new HwndSource(new HwndSourceParameters("CoderMascotHotkey")
        {
            // HWND_MESSAGE: a window that exists only to receive messages.
            ParentWindow = new IntPtr(-3),
            Width = 0,
            Height = 0,
        });
        _sink.AddHook(OnMessage);

        if (!RegisterHotKey(_sink.Handle, HotkeyId, spec.Modifiers | Hotkey.NoRepeat, spec.Key))
        {
            var reason = Marshal.GetLastWin32Error() == 1409
                ? "another application already uses it"
                : $"Windows refused it (error {Marshal.GetLastWin32Error()})";

            Dispose();
            return $"{spec.Text} could not be registered — {reason}.";
        }

        _registered = true;
        Bound = spec.Text;
        return null;
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || wParam.ToInt32() != HotkeyId) return IntPtr.Zero;

        handled = true;
        Pressed?.Invoke(this, EventArgs.Empty);
        return IntPtr.Zero;
    }

    private void Unregister()
    {
        if (_sink is null) return;

        if (_registered)
        {
            try { UnregisterHotKey(_sink.Handle, HotkeyId); } catch { /* handle already gone */ }
            _registered = false;
        }

        _sink.RemoveHook(OnMessage);
        _sink.Dispose();
        _sink = null;
        Bound = null;
    }

    public void Dispose() => Unregister();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
