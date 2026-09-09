namespace CoderMascot.Core;

/// <summary>
/// What kind of git command this is, from its arguments.
///
/// One distinction, and it earns its own file because getting it wrong shipped
/// a bug that made the feature impossible to use: a command that *reaches the
/// server* is not the same as one that reads what is already here.
///
/// It decides two things. How long to wait — a first fetch of a real repository
/// is minutes, a local read is milliseconds. And, for a repository read from a
/// URL, whether the guard applies: the guard exists so an empty copy never reads
/// as an empty repository, but a fetch is the thing that *fills* the copy, so
/// requiring a filled copy before allowing one is a circle with no way in.
/// </summary>
public static class GitCommand
{
    public static bool ReachesServer(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] is "fetch" or "clone" or "ls-remote" or "push" or "pull";
}
