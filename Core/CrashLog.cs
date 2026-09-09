using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace CoderMascot.Core;

/// <summary>
/// What happened, written down.
///
/// An app whose entire job is to notice failures cannot be the thing that
/// disappears without a word. Before this, an unhandled exception in a menu
/// handler took the whole process with it and left the user with nothing to
/// report but "it crashed" — the same silent-failure shape the mascot exists to
/// complain about, turned on itself.
///
/// Deliberately its own file next to the notes and the config, not the Windows
/// event log: the user can open it, read it and paste it somewhere.
/// </summary>
public static class CrashLog
{
    public static string Path_ => System.IO.Path.Combine(CoderConfig.Dir, "crash.log");

    /// <summary>Keep the last few incidents, not a year of them.</summary>
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    /// <summary>The most recent thing written, for the UI to offer to show.</summary>
    public static string? Last { get; private set; }

    /// <summary>Record one failure. Never throws — it is the last line of defence.</summary>
    public static void Write(string context, Exception? error)
    {
        var report = new StringBuilder()
            .Append("=== ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))
            .Append("  ").AppendLine(context)
            .Append("version ").Append(Version).Append("   os ").AppendLine(Environment.OSVersion.VersionString)
            .AppendLine(error?.ToString() ?? "(no exception object)")
            .AppendLine()
            .ToString();

        Last = report;
        Debug.WriteLine($"[CoderMascot] {context}: {error}");

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(CoderConfig.Dir);

                // Trim to the most recent half rather than rotate or blank it.
                // One file is one place to look; blanking would mean a crash
                // loop erases the incidents worth reading on its way past the
                // cap, which is the opposite of what a crash log is for.
                if (File.Exists(Path_) && new FileInfo(Path_).Length > MaxBytes)
                    File.WriteAllText(Path_, KeepTail(File.ReadAllText(Path_)));

                File.AppendAllText(Path_, report);
            }
        }
        catch
        {
            // Nowhere to write to. There is nothing left to try.
        }
    }

    /// <summary>The recent half, cut at an incident header so it stays readable.</summary>
    private static string KeepTail(string log)
    {
        var keep = log[^(int)Math.Min(log.Length, MaxBytes / 2)..];
        var start = keep.IndexOf("=== ", StringComparison.Ordinal);

        return "(earlier entries trimmed)\n\n" + (start >= 0 ? keep[start..] : keep);
    }

    private static string Version =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
}
