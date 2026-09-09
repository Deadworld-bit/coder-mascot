using System.Diagnostics;
using System.IO;
using System.Text;
using CoderMascot.Core;

namespace CoderMascot.UI;

/// <summary>
/// Actually running git — on this machine, or inside the workspace.
///
/// Both paths pass arguments as an argument *list*, never as a command string.
/// The repository path comes from a config file the user edits by hand, and the
/// branch names come from the repository itself; neither is hostile, but a
/// branch called `--upload-pack=…` is a real category of git argument-injection
/// and costs nothing to rule out.
/// </summary>
public static class GitRunners
{
    /// <summary>How long one git command may take before it is given up on.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The same, for a command that talks to a server.
    ///
    /// Much longer, because the first fetch of a repository is minutes of real
    /// work on a slow link and giving up on it at 45 seconds means the feature
    /// simply never works for a large repository. Every command with this
    /// patience is one the user asked for and is watching a note about.
    /// </summary>
    private static readonly TimeSpan FetchPatience = TimeSpan.FromMinutes(10);

    public static GitRun For(CoderConfig cfg) => async (project, args, ct) => project.Host switch
    {
        RepoHost.Workspace => await Workspace(cfg, project, args, ct),
        RepoHost.Remote => await Mirror(cfg, project, args, ct),
        _ => await Local(cfg, project, args, ct),
    };

    /// <summary>
    /// Make sure the local copy of a URL-only repository exists and points at
    /// the right place. Creating it is cheap; filling it is the fetch that
    /// follows, and that is the caller's next command.
    ///
    /// Deliberately not folded into the runner. A read must never reach the
    /// network on its own — opening a window would become an operation with side
    /// effects, and a slow one — so this is called from exactly two places, both
    /// of them a button the user pressed.
    /// </summary>
    public static async Task<GitOutput> PrepareMirror(
        CoderConfig cfg, GitProject project, CancellationToken ct)
    {
        if (GitCli.Resolve(cfg) is not { } git)
            return GitOutput.Bad("git isn't on this machine, so a repository URL can't be read. "
                                 + "Install it, or set \"gitPath\" in the config to its full path.");

        if (GitUrl.Clean(project.Path) is not { } url)
            return GitOutput.Bad(GitUrl.Inspect(project.Path).Problem ?? "That repository URL can't be used.");

        var dir = MirrorStore.PathFor(url);
        var fresh = !MirrorStore.HasRepository(url);

        try
        {
            Directory.CreateDirectory(MirrorStore.Root);
        }
        catch (Exception ex)
        {
            return GitOutput.Bad($"Couldn't make a place to keep the copy \u2014 {ex.Message}");
        }

        if (fresh)
        {
            // Bare, and fetched through an ordinary remote rather than cloned
            // with --mirror: that puts the server's branches under
            // refs/remotes/origin/* the way a normal clone does, so nothing
            // downstream has to know this repository is different, and no branch
            // here is ever mistaken for a local one you could safely delete.
            var init = await Run(git, [], ["init", "--bare", dir], ct);
            if (!init.Ok) return init;

            var remote = await Run(git, ["-C", dir], ["remote", "add", "origin", url], ct);
            if (!remote.Ok) return remote;

            // Commits and trees, no file contents. Every command this app runs
            // asks about ancestry and dates; none of them opens a file, so the
            // download is a fraction of the repository and stays that way.
            await Run(git, ["-C", dir], ["config", "remote.origin.promisor", "true"], ct);
            await Run(git, ["-C", dir], ["config", "remote.origin.partialclonefilter", "blob:none"], ct);
        }
        else
        {
            // The URL may have been edited since. Pointing the existing copy at
            // it beats re-downloading, and beats fetching from the old one.
            var repoint = await Run(git, ["-C", dir], ["remote", "set-url", "origin", url], ct);
            if (!repoint.Ok) return repoint;
        }

        return GitOutput.Good(dir);
    }

    /// <summary>
    /// Ask the server directly: can it be reached, and will it let us in?
    ///
    /// `ls-remote` and nothing else. It needs no local copy, downloads no
    /// objects and changes nothing, so it can be pressed as often as you like
    /// while sorting out a token — which is exactly what it is for.
    /// </summary>
    public static async Task<RemoteTest> TestRemote(CoderConfig cfg, string rawUrl, CancellationToken ct)
    {
        if (GitCli.Resolve(cfg) is not { } git)
            return RemoteTest.Failed(
                "git isn't on this machine, so nothing can be read from a URL. "
                + "Install it, or set \"gitPath\" in the config to its full path.", null);

        if (GitUrl.Clean(rawUrl) is not { } url)
            return RemoteTest.Failed(
                GitUrl.Inspect(rawUrl).Problem ?? "That repository URL can't be used.", null);

        var seen = await Run(git, [], ["ls-remote", "--heads", url], ct);
        return RemoteTest.From(seen.Ok, seen.Text, seen.Error, url);
    }

    /// <summary>
    /// Hand a username and token to Git's own credential store.
    ///
    /// Null when it worked. The helper is checked first and its absence is a
    /// failure, not a shrug: `git credential approve` with nothing configured
    /// exits 0 and stores nothing, so trusting its exit code would have this
    /// window report "signed in" and then fail the next fetch identically.
    /// </summary>
    public static async Task<string?> StoreCredential(
        CoderConfig cfg, string url, string username, string secret, CancellationToken ct)
    {
        if (GitCli.Resolve(cfg) is not { } git) return "git isn't on this machine.";

        if (GitUrl.Clean(url) is not { } clean)
            return GitUrl.Inspect(url).Problem ?? "That repository URL can't be used.";

        if (GitSignIn.Payload(clean, username, secret) is not { } payload)
            return "That username or token has a line break in it. Paste it again without one.";

        var helper = await Run(git, [], ["config", "--get-urlmatch", "credential.helper", clean], ct);
        if (!helper.Ok || helper.Text.Trim().Length == 0) return GitSignIn.NoStore;

        // The secret goes down stdin, never into an argument: a command line is
        // readable by every other process on the machine.
        var psi = new ProcessStartInfo(git)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }.ReadAsUtf8();

        psi.ArgumentList.Add("credential");
        psi.ArgumentList.Add("approve");

        var stored = await Capture(psi, ct, input: payload);
        return stored.Ok ? null : Trim(stored.Error);
    }

    private static string Trim(string text)
    {
        var clean = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return clean.Length > 200 ? clean[..200] + "\u2026" : clean;
    }

    private static async Task<GitOutput> Mirror(
        CoderConfig cfg, GitProject project, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (GitCli.Resolve(cfg) is not { } git)
            return GitOutput.Bad("git isn't on this machine, so a repository URL can't be read. "
                                 + "Install it, or set \"gitPath\" in the config to its full path.");

        if (GitUrl.Clean(project.Path) is not { } url)
            return GitOutput.Bad(GitUrl.Inspect(project.Path).Problem ?? "That repository URL can't be used.");

        if (MirrorStore.Refuse(url, GitCommand.ReachesServer(args)) is { } refused)
            return GitOutput.Bad(refused);

        return await Run(git, ["-C", MirrorStore.PathFor(url)], args, ct);
    }

    /// <summary>
    /// Repositories worth offering in the Add-repository box.
    ///
    /// An assist, never an import: the list is somewhere to click, and what gets
    /// watched is still only what the user picks. That distinction is the whole
    /// reason the config is hand-written — a scan finds every checkout you have
    /// ever made, and the list you want is the four you are shipping from.
    ///
    /// A workspace path is the case that needs this most. It is a Linux path
    /// being typed on a Windows keyboard into a box that cannot autocomplete it,
    /// and one wrong character produces "doesn't exist in the workspace" with no
    /// clue as to which character.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindRepositories(
        CoderConfig cfg, RepoHost host, CancellationToken ct)
    {
        // Off the UI thread: six directory listings is fast until one of the
        // roots is a network drive that has gone away.
        if (host == RepoHost.Local) return await Task.Run(LocalCheckouts, ct);

        if (CoderCli.Resolve(cfg) is not { } cli) return [];

        var target = (cfg.Workspace ?? string.Empty).Trim();
        if (target.Length == 0 || target.StartsWith('-')) return [];

        var psi = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }.ReadAsUtf8();

        psi.ArgumentList.Add("ssh");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("--");

        // Fixed text, with nothing of the user's interpolated into it. The two
        // roots are the workspace layout every Coder workspace here is built
        // with; anything kept elsewhere is still typed in by hand.
        psi.ArgumentList.Add(
            "for d in \"$HOME\"/workspace/projects/*/ \"$HOME\"/workspace/share-projects/*/; " +
            "do [ -d \"$d.git\" ] && printf '%s\\n' \"${d%/}\"; done");

        var found = await Capture(psi, ct);
        if (!found.Ok) return [];

        return [.. found.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimEnd('\r'))
            .Where(line => line.StartsWith('/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>One level down the folders people actually keep checkouts in.</summary>
    private static IReadOnlyList<string> LocalCheckouts()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return [];

        var roots = new[]
        {
            Path.Combine(home, "source", "repos"),
            Path.Combine(home, "Projects"),
            Path.Combine(home, "projects"),
            Path.Combine(home, "src"),
            Path.Combine(home, "repos"),
            Path.Combine(home, "Documents", "GitHub"),
        };

        var found = new List<string>();

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;

                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (Directory.Exists(Path.Combine(dir, ".git"))) found.Add(dir);
                }
            }
            catch
            {
                // A root we can't read is a root we don't offer. Browse… still works.
            }
        }

        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<GitOutput> Local(
        CoderConfig cfg, GitProject project, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!Directory.Exists(project.Path))
            return GitOutput.Bad($"{project.Path} doesn't exist on this machine.");

        // Never the bare name: CreateProcess would search this app's own folder
        // first, and this app ships as a portable folder people drop in
        // Downloads. Same rule as the coder CLI, one resolver each.
        if (GitCli.Resolve(cfg) is not { } git)
            return GitOutput.Bad("git isn't on this machine, so local repositories can't be read. "
                                 + "Install it, or set \"gitPath\" in the config to its full path.");

        // -C rather than a working directory, so the repository is named in the
        // command itself. Note this does NOT catch a path inside a larger
        // repository — git walks up to the enclosing .git either way, and
        // reports that repository's branches.
        return await Run(git, ["-C", project.Path], args, ct);
    }

    /// <summary>One git process: fixed arguments first, then the command's own.</summary>
    private static async Task<GitOutput> Run(
        string git, IReadOnlyList<string> lead, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(git)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }.ReadAsUtf8();

        // Off, so a file or branch with an accent in it comes back as itself
        // rather than as \303\251 escapes that no filter will ever match.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");

        foreach (var arg in lead) psi.ArgumentList.Add(arg);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        return await Capture(psi, ct, GitCommand.ReachesServer(args) ? FetchPatience : Patience);
    }

    private static async Task<GitOutput> Workspace(
        CoderConfig cfg, GitProject project, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (CoderCli.Resolve(cfg) is not { } cli)
            return GitOutput.Bad("The coder CLI isn't on this machine, so workspace repositories can't be read.");

        var target = (cfg.Workspace ?? string.Empty).Trim();
        if (target.Length == 0) return GitOutput.Bad("No workspace name configured.");
        if (target.StartsWith('-')) return GitOutput.Bad("That workspace name can't be used.");

        var psi = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }.ReadAsUtf8();

        psi.ArgumentList.Add("ssh");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("--");

        // The far side is a login shell, so everything it receives is re-parsed
        // by bash. Each argument is single-quoted and any embedded quote is
        // closed and re-opened around an escaped one — the standard, and the
        // only safe, way to hand an arbitrary string to a shell.
        var command = new StringBuilder("git -C ").Append(Quote(project.Path));
        foreach (var arg in args) command.Append(' ').Append(Quote(arg));

        psi.ArgumentList.Add(command.ToString());

        return await Capture(psi, ct);
    }

    /// <summary>Wrap a string so bash reads it back as exactly this string.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static async Task<GitOutput> Capture(
        ProcessStartInfo psi, CancellationToken ct, TimeSpan? patience = null, string? input = null)
    {
        // A private server will ask who you are. Git's credential manager can
        // answer with a window; a *terminal* prompt cannot, because there is no
        // terminal here — it would sit on a redirected pipe until the timeout,
        // which reads as the app having hung. Refusing that turns a silent wait
        // into an error message naming the repository.
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(patience ?? Patience);

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null) return GitOutput.Bad("Couldn't start git.");

            // Written and closed before the output is read: git waits for end of
            // input before it answers, so holding the pipe open would deadlock
            // against our own read.
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }

            // Both streams are read before waiting: a command that fills the
            // error pipe while nobody drains it deadlocks, and `git log` on a
            // big repository fills a pipe easily.
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token);

            var text = await stdout;
            var error = await stderr;

            var ok = process.ExitCode == 0;
            var trouble = error.Length > 0 ? error : $"git exited with {process.ExitCode}.";

            // Kept whatever the outcome: a command that succeeded is context for
            // the one after it that didn't. Arguments only — never stdin, which
            // is where the password went precisely so it would be nowhere else.
            GitLog.Note(string.Join(' ', psi.ArgumentList), ok, ok ? string.Empty : trouble);

            return ok ? GitOutput.Good(text) : GitOutput.Bad(trouble);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return GitOutput.Bad("Cancelled.");
        }
        catch (OperationCanceledException)
        {
            var gaveUp = "git took too long and was given up on.";
            GitLog.Note(string.Join(' ', psi.ArgumentList), ok: false, gaveUp);
            return GitOutput.Bad(gaveUp);
        }
        catch (Exception ex)
        {
            GitLog.Note(string.Join(' ', psi.ArgumentList), ok: false, ex.Message);
            return GitOutput.Bad(ex.Message);
        }
        finally
        {
            // Giving up on the wait does not stop the process. An abandoned
            // `coder ssh` holds a connection open, and one per refresh adds up
            // to a machine full of them.
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone, or not ours to kill.
                }

                process.Dispose();
            }
        }
    }
}
