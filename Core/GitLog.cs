using System.IO;
using System.Text;

namespace CoderMascot.Core;

/// <summary>One git command that was run, and what came back.</summary>
public sealed record GitStep(DateTimeOffset When, string Command, bool Ok, string Error);

/// <summary>
/// The last few git commands, so "it doesn't work" can be answered.
///
/// Every friendly sentence this app shows is a translation of something git
/// said, and a translation loses the detail that identifies the actual fault:
/// "the server wants a sign-in" covers a wrong token, a token with the wrong
/// scopes, a repository you can't see, and a proxy in the way. This keeps the
/// original text so the window can show it on request — which is the difference
/// between diagnosing a problem and guessing at it.
///
/// What is never kept: anything sent on stdin. That is where the password goes,
/// deliberately, and it stays out of here for the same reason it stays off the
/// command line. Credentials embedded in a URL are stripped on the way in, in
/// case one ever reaches an argument.
/// </summary>
public static class GitLog
{
    private const int Keep = 60;

    private static readonly object Gate = new();
    private static readonly Queue<GitStep> Steps = new();

    public static void Note(string command, bool ok, string error)
    {
        var step = new GitStep(DateTimeOffset.Now, Redact(command), ok, Redact(Shorten(error)));

        lock (Gate)
        {
            Steps.Enqueue(step);
            while (Steps.Count > Keep) Steps.Dequeue();
        }
    }

    public static IReadOnlyList<GitStep> Recent(int most = Keep)
    {
        lock (Gate)
        {
            return [.. Steps.Reverse().Take(Math.Max(1, most))];
        }
    }

    /// <summary>The recent history as something to read, or paste into a message.</summary>
    public static string Report(int most = 14)
    {
        var steps = Recent(most);
        if (steps.Count == 0) return "Nothing has been run yet.";

        var text = new StringBuilder();

        foreach (var step in steps)
        {
            text.Append(step.When.ToString("HH:mm:ss"))
                .Append(step.Ok ? "  ok    git " : "  FAIL  git ")
                .Append(step.Command)
                .Append('\n');

            if (!step.Ok && step.Error.Length > 0)
                text.Append("            ").Append(step.Error.Replace("\n", "\n            ")).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Alongside the crash log, so it can be sent rather than retyped.</summary>
    public static string? Save()
    {
        try
        {
            Directory.CreateDirectory(CoderConfig.Dir);

            var path = Path.Combine(CoderConfig.Dir, "git-log.txt");
            File.WriteAllText(path, Report(Keep));

            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string Shorten(string error)
    {
        var clean = (error ?? string.Empty).Trim();
        return clean.Length > 600 ? clean[..600] + "…" : clean;
    }

    /// <summary>
    /// Take any "https://user:secret@host/…" back down to "https://host/…".
    ///
    /// Nothing is supposed to put a credential in an argument — GitUrl strips
    /// them and the password travels on stdin — but a log is read by people and
    /// pasted into chat windows, so it does not rely on that holding.
    /// </summary>
    private static string Redact(string text)
    {
        if (!text.Contains("://", StringComparison.Ordinal) || !text.Contains('@')) return text;

        var parts = text.Split(' ');

        for (var i = 0; i < parts.Length; i++)
        {
            var scheme = parts[i].IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0) continue;

            var start = scheme + 3;
            var end = parts[i].IndexOf('/', start);
            var host = end < 0 ? parts[i][start..] : parts[i][start..end];

            var at = host.LastIndexOf('@');
            if (at < 0) continue;

            parts[i] = parts[i][..start] + "***@" + host[(at + 1)..] + (end < 0 ? string.Empty : parts[i][end..]);
        }

        return string.Join(' ', parts);
    }
}
