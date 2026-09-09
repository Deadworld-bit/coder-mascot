namespace CoderMascot.Core;

/// <summary>
/// Handing a username and token to Git's own credential store, and recognising
/// when git is asking for one.
///
/// The secret is never kept by this app. It goes to whatever credential helper
/// git is configured with — on Windows that is Git Credential Manager, which
/// keeps it in Windows Credential Manager, encrypted for this user account —
/// and from then on every git tool on the machine can sign in with it, not just
/// this one. config.json stays what it says it is: plain settings.
///
/// The catch that shapes all of this: `git credential approve` with no helper
/// configured exits 0 and does nothing at all. Reporting that as "saved" would
/// be a lie the user only discovers when the next fetch fails the same way, so
/// the helper is checked first and its absence is said out loud.
/// </summary>
public static class GitSignIn
{
    /// <summary>
    /// The stdin git expects, or null if the fields can't safely be sent.
    ///
    /// The protocol is line-based key=value, so a newline inside a value would
    /// start a new key — a password containing one could set any other field.
    /// Nothing here is trusted enough to be pasted in unchecked.
    /// </summary>
    public static string? Payload(string url, string username, string secret)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (string.IsNullOrEmpty(secret)) return null;

        var user = username.Trim();

        if (Unsafe(url) || Unsafe(user) || Unsafe(secret)) return null;

        // url= is git's own shorthand: it splits into protocol, host and path
        // itself, so the key this is stored under is exactly the key the fetch
        // will look for. Building those fields by hand is how you end up with a
        // credential saved for a host that is spelled slightly differently.
        var lines = new List<string> { $"url={url}" };
        if (user.Length > 0) lines.Add($"username={user}");
        lines.Add($"password={secret}");

        return string.Join('\n', lines) + "\n\n";
    }

    private static bool Unsafe(string value) => value.Any(char.IsControl);

    /// <summary>
    /// Is git asking to be told who we are?
    ///
    /// Worth telling apart from every other failure, because it is the one with
    /// an answer on screen: a fetch that says "could not read Username" is not a
    /// broken repository, a missing branch or a network fault, and reporting
    /// git's own sentence for it sends people looking in the wrong place.
    /// </summary>
    public static bool Needed(string? error)
    {
        var text = error ?? string.Empty;

        return Says(text, "could not read Username")
               || Says(text, "could not read Password")
               || Says(text, "Authentication failed")
               || Says(text, "terminal prompts disabled")
               || Says(text, "Invalid username or password")
               || Says(text, "HTTP Basic: Access denied")
               || Says(text, "The requested URL returned error: 401")
               || Says(text, "The requested URL returned error: 403")
               || Says(text, "Permission denied (publickey")
               || Says(text, "Repository not found");
    }

    private static bool Says(string text, string phrase) =>
        text.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Signed in, but this account cannot have it.
    ///
    /// Worth its own answer: a token that is *wrong* and a token that is right
    /// but lacks read scope — or names an account with no access to that project
    /// — produce the same shrug from a window that only knows "sign-in
    /// problem", and they need opposite things done about them.
    /// </summary>
    public static bool Forbidden(string? error)
    {
        var text = error ?? string.Empty;

        return Says(text, "The requested URL returned error: 403")
               || Says(text, "Repository not found")
               || Says(text, "You are not allowed")
               || Says(text, "insufficient scope")
               || Says(text, "access denied or repository not exported");
    }

    /// <summary>The network never got there at all.</summary>
    public static bool Unreachable(string? error)
    {
        var text = error ?? string.Empty;

        return Says(text, "Could not resolve host")
               || Says(text, "Failed to connect")
               || Says(text, "Connection refused")
               || Says(text, "Connection timed out")
               || Says(text, "Operation timed out")
               || Says(text, "Network is unreachable")
               || Says(text, "Couldn't connect to server");
    }

    /// <summary>The certificate on the way is not one git trusts here.</summary>
    public static bool Untrusted(string? error) =>
        Says(error ?? string.Empty, "SSL certificate problem")
        || Says(error ?? string.Empty, "unable to get local issuer certificate")
        || Says(error ?? string.Empty, "self signed certificate");

    /// <summary>What to put on screen when a fetch failed for want of a sign-in.</summary>
    public const string Ask =
        "The server wants a sign-in. Fill in the username and token below, then press Download again.";

    /// <summary>And when there is nowhere to put one.</summary>
    public const string NoStore =
        "Git has no credential store set up on this machine, so there is nowhere safe to keep this. "
        + "Run  git config --global credential.helper manager  once in a terminal, then try again.";
}
