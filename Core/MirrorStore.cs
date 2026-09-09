using System.IO;

namespace CoderMascot.Core;

/// <summary>
/// The local copy of a repository that is only ever read from its URL.
///
/// A bare clone, kept under %LOCALAPPDATA%, with the remote's branches under
/// refs/remotes/origin/* exactly as an ordinary clone has them — which is why
/// every command the reader already runs works against it untouched, and why a
/// branch here is never mistaken for a local one you could delete.
///
/// It exists because the alternatives are slow in a way that shows. A repository
/// in the workspace costs a `coder ssh` round trip per command, six per read; a
/// repository on this PC needs a full working tree checked out. This needs
/// neither: one fetch of commits and trees — no file contents at all — and then
/// every read after it is a local process against a few megabytes.
///
/// It is a cache, so it is never the only copy of anything, and deleting the
/// folder costs nothing but the next fetch.
/// </summary>
public static class MirrorStore
{
    /// <summary>
    /// Where the copies live.
    ///
    /// LocalApplicationData, not roaming: this is a cache, and nobody wants it
    /// following them onto another machine. The fallback is not decoration —
    /// GetFolderPath returns an empty string when the OS can't offer that
    /// folder, and Path.Combine would quietly turn that into a *relative* path,
    /// scattering bare repositories into whatever directory the app happens to
    /// have been started from.
    /// </summary>
    public static string Root
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(
                string.IsNullOrWhiteSpace(local) ? CoderConfig.Dir : Path.Combine(local, "CoderMascot"),
                "mirrors");
        }
    }

    public static string PathFor(string url) => Path.Combine(Root, GitUrl.Folder(url));

    /// <summary>
    /// A git repository has been created here. Says nothing about its contents.
    ///
    /// This is the question the runner asks — is there somewhere for `git -C` to
    /// point — and only that. It is true the instant `init --bare` has run, so
    /// it is emphatically not the same as <see cref="Fetched"/>.
    /// </summary>
    public static bool HasRepository(string url)
    {
        try { return Directory.Exists(Path.Combine(PathFor(url), "objects")); }
        catch { return false; }
    }

    /// <summary>
    /// A fetch has actually completed against this URL at least once.
    ///
    /// Recorded by us, in a file of our own, because git leaves no honest trace
    /// of it. `init --bare` creates objects/ immediately, so the folder proves
    /// nothing; and a fetch that dies asking for a password writes FETCH_HEAD
    /// anyway, so *that* proves nothing either. Between them those two facts
    /// turn a failed sign-in into a repository that reports, cheerfully and in
    /// green, that it has no branches — which is exactly the "couldn't find out"
    /// rendered as an answer that nothing in this window is allowed to do.
    /// </summary>
    public static bool Fetched(string url) => FetchedAt(url) is not null;

    /// <summary>
    /// Why this copy can't be read yet, or null when it can.
    ///
    /// One definition, so the runner's guard and the window's wording cannot
    /// drift apart — and so the case the user actually hit (a folder that
    /// exists, a fetch that never worked) is a thing with a test rather than
    /// three lines of condition inside a process launcher.
    /// </summary>
    public static string? NotReady(string url) =>
        HasRepository(url) && Fetched(url)
            ? null
            : "This repository hasn't been downloaded yet. "
              + "Press \u201cFetch from origin\u201d to fetch it once.";

    /// <summary>
    /// Why a command can't run against this copy yet, or null when it can.
    ///
    /// `filling` is the whole of it. A read must be refused until something has
    /// actually been fetched, or an empty copy reads as an empty repository. A
    /// fetch is the opposite: it is what makes the copy non-empty, so applying
    /// the same rule to it is a circle — the first download can never happen,
    /// and the window sits there telling you to press the button you just
    /// pressed. It needs only somewhere to fetch into.
    /// </summary>
    public static string? Refuse(string url, bool filling) =>
        filling
            ? HasRepository(url) ? null : "There is nowhere to fetch into yet. Press Download again."
            : NotReady(url);

    /// <summary>When the last fetch that actually worked finished, or null.</summary>
    public static DateTimeOffset? FetchedAt(string url)
    {
        try
        {
            var stamp = Stamp(url);
            return File.Exists(stamp) ? new DateTimeOffset(File.GetLastWriteTime(stamp)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Write that down. Called only where a fetch has returned success.</summary>
    /// <summary>
    /// Take the mark back off, because the copy turned out not to be usable.
    ///
    /// Needed as much as writing it. A stamp claiming a download that produced
    /// nothing readable outlives the attempt that wrote it, and every later read
    /// then reports an empty repository as a finding rather than as a copy that
    /// has to be fetched again.
    /// </summary>
    public static void ClearFetched(string url)
    {
        try
        {
            var stamp = Stamp(url);
            if (File.Exists(stamp)) File.Delete(stamp);
        }
        catch
        {
            // Same reasoning as writing it: the mark is a convenience, and
            // failing to remove it is not worth taking the window down for.
        }
    }

    public static void MarkFetched(string url)
    {
        try
        {
            var dir = PathFor(url);
            if (!Directory.Exists(dir)) return;

            File.WriteAllText(Stamp(url),
                "Written by Coder Mascot after a fetch that succeeded. Safe to delete.\n"
                + DateTimeOffset.Now.ToString("O"));
        }
        catch
        {
            // Losing the marker costs one unnecessary re-fetch, not correctness.
        }
    }

    private static string Stamp(string url) => Path.Combine(PathFor(url), "mascot-fetched");

    /// <summary>Roughly what it is costing in disk, for the line that says so.</summary>
    public static long Bytes(string url)
    {
        try
        {
            var dir = PathFor(url);
            if (!Directory.Exists(dir)) return 0;

            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try { return new FileInfo(file).Length; }
                    catch { return 0L; }
                });
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>"12 MB", for a sentence rather than a table.</summary>
    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "nothing yet",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}
