using System.Diagnostics;

namespace CoderMascot.Core;

/// <summary>
/// Closing things, on the user's explicit instruction.
///
/// The rule the whole file exists to enforce: <b>never Kill</b>. Everything the
/// mascot offers to close is an editor, a dev server or a watcher — things with
/// unsaved buffers and child processes. CloseMainWindow is the same signal as
/// clicking the X, so VS Code still asks about unsaved files and a terminal
/// still runs its shell's exit path. A tidy-up feature that can silently destroy
/// an hour of work is not a tidy-up feature, it is a bug with a button.
///
/// A console process with no message loop simply won't go. Saying so is the
/// honest outcome, and better than escalating behind the user's back.
/// </summary>
public static class ProcessControl
{
    /// <summary>Ask one process to close. Returns a line to show, or null on success.</summary>
    public static string? Close(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return null;

            var name = p.ProcessName;
            return p.CloseMainWindow()
                ? null
                : $"{name} has no window to close — stop it from its own terminal.";
        }
        catch (ArgumentException)
        {
            return null;      // already gone: that is the outcome we wanted
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Ask every process of a given name to close — the "Code ×7" case.
    /// Returns how many were asked, and a line if any refused.
    /// </summary>
    public static (int Asked, string? Problem) CloseFamily(string name)
    {
        var asked = 0;
        string? problem = null;

        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.HasExited) continue;
                    if (p.CloseMainWindow()) asked++;
                    else problem ??= $"Some {name} processes have no window to close.";
                }
                catch
                {
                    // Exited underneath us, or protected. Not worth a word.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            return (asked, ex.Message);
        }

        return (asked, problem);
    }
}
