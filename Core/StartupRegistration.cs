using System.Diagnostics;
using Microsoft.Win32;

namespace CoderMascot.Core;

/// <summary>
/// Runs the mascot when you log in.
///
/// One HKCU Run value, no scheduled task and no shortcut in the Startup folder:
/// this is the mechanism Windows itself shows in Task Manager's Startup tab, so
/// the user can see it, disable it, and be believed when they do.
///
/// The config file holds the *intent* and this holds the mechanism, which is why
/// <see cref="Sync"/> exists — the two can drift apart on their own. Moving or
/// renaming the exe leaves the registry pointing at a path that no longer runs
/// anything, and the failure is silent: nothing appears at logon and the app
/// looks like it simply stopped honouring the setting.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CoderMascot";

    /// <summary>What the Run value should say for the copy that's running now.</summary>
    public static string? Command =>
        Environment.ProcessPath is { Length: > 0 } exe ? $"\"{exe}\"" : null;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Turn it on or off. Returns null on success, or why it failed.</summary>
    public static string? Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                            ?? throw new InvalidOperationException("Run key unavailable.");

            if (!enable)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return null;
            }

            if (Command is not { } command)
                return "Could not determine this executable's path.";

            key.SetValue(ValueName, command);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Make the registry agree with the config, at every launch.
    ///
    /// Also rewrites a value that points somewhere else, which is the only way a
    /// user who moved the folder ever finds out. It writes only when the value
    /// is actually wrong, so a startup entry the user disabled from Task Manager
    /// — which keeps the value and flags it elsewhere — is left exactly as it is
    /// rather than being re-enabled behind their back on every login.
    /// </summary>
    public static void Sync(CoderConfig cfg)
    {
        try
        {
            var wanted = cfg.StartWithWindows;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            var current = key?.GetValue(ValueName) as string;

            if (!wanted)
            {
                if (current is not null) Set(false);
                return;
            }

            if (current is null || !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                Set(true);
        }
        catch (Exception ex)
        {
            // A locked-down or policy-managed machine can refuse this. Not being
            // able to arrange the next launch is no reason to spoil this one
            // with a crash dialog before the tray icon even exists.
            Debug.WriteLine($"[CoderMascot] startup sync failed: {ex.Message}");
        }
    }
}
