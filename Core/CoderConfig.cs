using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoderMascot.Core;

/// <summary>
/// Where the mascot points and how it authenticates.
///
/// Resolution order for URL + token:
///   1. %APPDATA%\CoderMascot\config.json
///   2. CODER_URL / CODER_SESSION_TOKEN environment variables
///   3. the Coder CLI's own session, i.e. whatever `coder login` already wrote
///      (config dir "coderv2", files "url" and "session")
///
/// (3) is the reason this needs no setup on a machine that already uses the CLI.
/// </summary>
public sealed class CoderConfig
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }

    /// <summary>Workspace owner username. Empty means "me".</summary>
    [JsonPropertyName("owner")] public string? Owner { get; set; }

    /// <summary>Workspace name. Empty means auto-detect when you own exactly one.</summary>
    [JsonPropertyName("workspace")] public string? Workspace { get; set; }

    [JsonPropertyName("pollSeconds")] public int PollSeconds { get; set; } = 20;

    /// <summary>Warn this many minutes before Coder auto-stops the workspace.</summary>
    [JsonPropertyName("autoStopWarnMinutes")] public int AutoStopWarnMinutes { get; set; } = 15;

    /// <summary>
    /// How many consecutive bad polls before the mascot raises an alarm. Guards
    /// against a single dropped packet turning into a false "you're disconnected".
    /// </summary>
    [JsonPropertyName("failuresBeforeAlarm")] public int FailuresBeforeAlarm { get; set; } = 2;

    [JsonPropertyName("mascotLeft")] public double? MascotLeft { get; set; }
    [JsonPropertyName("mascotTop")] public double? MascotTop { get; set; }

    /// <summary>
    /// Launch with Windows. On by default: an app whose whole job is to tell you
    /// the workspace died is useless on the day you forget to start it, and the
    /// day you forget is exactly the day it was worth having.
    ///
    /// Turning it off in either menu writes false here, and the registry follows
    /// this on every launch — see <see cref="StartupRegistration.Sync"/>.
    /// </summary>
    [JsonPropertyName("startWithWindows")] public bool StartWithWindows { get; set; } = true;

    /// <summary>
    /// Which characters to run, by id. More than one runs them all at once, each
    /// walking its own route.
    ///
    /// They share a single workspace poll and a single session poll no matter
    /// how many are on screen — the characters are a display concern, and a
    /// second mascot must not mean a second `coder ssh` every 30 seconds.
    /// </summary>
    [JsonPropertyName("characters")] public string[] Characters { get; set; } = ["pikachu", "charizard", "mew"];

    /// <summary>
    /// Where each character was last dropped, keyed by id.
    ///
    /// Per character rather than one shared pair, or two mascots would fight
    /// over the same saved spot and the second to be dragged would decide where
    /// both start next time.
    /// </summary>
    [JsonPropertyName("mascotPositions")]
    public Dictionary<string, double[]> MascotPositions { get; set; } = new();

    /// <summary>
    /// On-screen size of the mascot window, in device-independent pixels.
    ///
    /// The character's body is roughly half of this — the frame also has to
    /// contain the full sweep of the ears and tail, which are what got clipped
    /// when the frame was sized to the body alone.
    /// </summary>
    [JsonPropertyName("mascotSize")] public double MascotSize { get; set; } = 96;

    /// <summary>Walk around the screen edges instead of sitting in one spot.</summary>
    [JsonPropertyName("patrol")] public bool Patrol { get; set; } = true;

    /// <summary>Patrol speed in pixels per second.</summary>
    [JsonPropertyName("patrolSpeed")] public double PatrolSpeed { get; set; } = 90;

    /// <summary>"virtual" walks every monitor; "primary" stays on the main one.</summary>
    [JsonPropertyName("patrolBounds")] public string PatrolBounds { get; set; } = "virtual";

    /// <summary>
    /// Keep the mascot out of screenshots and screen shares. On by default: a
    /// mascot walking across a shared presentation is a worse surprise than not
    /// being able to screen-record it. Turn off to demo it to someone.
    /// </summary>
    [JsonPropertyName("hideFromScreenCapture")] public bool HideFromScreenCapture { get; set; } = true;

    /// <summary>Hide while a game, video or presentation is full-screen.</summary>
    [JsonPropertyName("hideWhenFullScreen")] public bool HideWhenFullScreen { get; set; } = true;

    // ---------- Claude session watch ----------

    /// <summary>
    /// Watch Claude Code sessions inside the workspace. Needs the hooks from
    /// workspace/install-hooks.sh, and the coder CLI on this machine.
    /// </summary>
    [JsonPropertyName("watchSessions")] public bool WatchSessions { get; set; } = true;

    /// <summary>
    /// Slower than the workspace poll on purpose: each check spawns a
    /// `coder ssh` process, which is not free.
    /// </summary>
    [JsonPropertyName("sessionPollSeconds")] public int SessionPollSeconds { get; set; } = 30;

    /// <summary>
    /// How long a session may sit silent *with a tool running* before it's
    /// called stuck. Generous, because a long build or test run genuinely emits
    /// nothing for minutes and a false alarm is worse than a late one.
    /// </summary>
    [JsonPropertyName("sessionStalledSeconds")] public int SessionStalledSeconds { get; set; } = 600;

    /// <summary>
    /// The same, for a session that is *thinking* — no tool in flight.
    ///
    /// Much shorter, because the two silences mean opposite things. Nothing is
    /// running, so the only thing that can be slow is the model call, and one
    /// number patient enough for a 15-minute build will not report a dead
    /// request until long after you've noticed it yourself.
    /// </summary>
    [JsonPropertyName("sessionThinkingStalledSeconds")]
    public int SessionThinkingStalledSeconds { get; set; } = 180;

    // ---------- this machine's own load ----------

    /// <summary>
    /// Warn when this PC — not the workspace — has been pinned for a while.
    ///
    /// For the editors and dev servers that accumulate across projects and
    /// never get closed. Each is cheap; a dozen is not.
    /// </summary>
    [JsonPropertyName("watchResources")] public bool WatchResources { get; set; } = true;

    [JsonPropertyName("cpuWarnPercent")] public double CpuWarnPercent { get; set; } = 88;
    [JsonPropertyName("memoryWarnPercent")] public double MemoryWarnPercent { get; set; } = 88;
    [JsonPropertyName("loadPollSeconds")] public int LoadPollSeconds { get; set; } = 10;

    /// <summary>
    /// How long the load has to *stay* up before it's worth saying anything.
    ///
    /// The single most important number here. A build pegs every core for a
    /// minute, and a warning that fires on that is one you learn to ignore
    /// within a day — at which point it may as well not exist.
    /// </summary>
    [JsonPropertyName("loadSustainSeconds")] public int LoadSustainSeconds { get; set; } = 120;

    // ---------- things left running ----------

    /// <summary>
    /// Watch for a workspace nobody is using and dev servers nobody is talking
    /// to. Neither is a fault; both are quota and memory spent on nothing.
    /// </summary>
    [JsonPropertyName("watchLeftovers")] public bool WatchLeftovers { get; set; } = true;

    /// <summary>
    /// Mention the workspace once it has gone this long unused. 0 turns it off.
    ///
    /// Measured from Coder's own last_used_at, so it already accounts for
    /// code-server and SSH — not just Claude sessions.
    /// </summary>
    [JsonPropertyName("workspaceIdleMinutes")] public int WorkspaceIdleMinutes { get; set; } = 45;

    /// <summary>
    /// How long a dev server has to have been listening before it counts as
    /// forgotten rather than in use. 0 turns it off.
    ///
    /// You are supposed to have one running while you work, so this number is
    /// what separates the feature from a warning about doing your job.
    /// </summary>
    [JsonPropertyName("devServerIdleMinutes")] public int DevServerIdleMinutes { get; set; } = 120;

    /// <summary>Say so when a Claude session finishes its turn.</summary>
    [JsonPropertyName("announceFinished")] public bool AnnounceFinished { get; set; } = true;

    // ---------- how loud ----------

    /// <summary>
    /// How much the mascot is allowed to interrupt: "all", "quiet" or "off".
    ///
    /// "quiet" keeps the badge colour and keeps the mascot stopping to be
    /// noticed, but stops the toasts, the speech bubbles and the reminders —
    /// for when you already know what it's going to say.
    /// </summary>
    [JsonPropertyName("notifications")] public string Notifications { get; set; } = "all";

    // ---------- nudges ----------

    /// <summary>
    /// How long something can sit unanswered before the mascot brings it up
    /// again. Zero turns reminders off and leaves the single notification.
    /// </summary>
    [JsonPropertyName("remindAfterSeconds")] public int RemindAfterSeconds { get; set; } = 120;

    /// <summary>The longest the gap between reminders is allowed to grow to.</summary>
    [JsonPropertyName("remindMaxSeconds")] public int RemindMaxSeconds { get; set; } = 900;

    /// <summary>
    /// System-wide shortcut for a new sticky note, e.g. "Ctrl+Alt+N". Blank
    /// turns it off — the tray and mascot menus still make notes.
    /// </summary>
    [JsonPropertyName("noteHotkey")] public string NoteHotkey { get; set; } = Hotkey.Default;

    /// <summary>
    /// Repositories to keep an eye on, and what deployment means in each.
    ///
    /// Chosen rather than discovered: a scan of the disk finds every checkout
    /// you have ever made, and the list you want is the four you are actually
    /// shipping from. The editor in the Branches window offers what it finds and
    /// writes down only what you pick, which is the same rule with less typing.
    ///
    /// Written through <see cref="SetProjects"/>, never assigned directly, so
    /// the editor's output goes through the same sieve as the file's.
    /// </summary>
    [JsonPropertyName("projects")] public ProjectEntry[] Projects { get; set; } = [];

    /// <summary>
    /// Group headings the rail is showing folded shut, by <see cref="ProjectGroups.Key"/>.
    ///
    /// Kept in the settings rather than in the window, because a fold that
    /// forgets itself every launch is a fold nobody uses twice. Pruned on load
    /// to the groups that actually exist.
    /// </summary>
    [JsonPropertyName("collapsedGroups")] public string[] CollapsedGroups { get; set; } = [];

    [JsonPropertyName("coderCli")] public string CoderCli { get; set; } = "coder";

    /// <summary>
    /// Full path to git.exe, for an install the search doesn't find. Blank means
    /// look in the usual places — never the app's own folder, see
    /// <see cref="GitCli"/>.
    /// </summary>
    [JsonPropertyName("gitPath")] public string? GitPath { get; set; }

    [JsonPropertyName("sessionSummaryScript")]
    public string SessionSummaryScript { get; set; } = "/home/coder/.claude/mascot/mascot-summary.js";

    /// <summary>
    /// True when Url/Token were borrowed from the environment or the Coder CLI
    /// rather than typed into our own config. Borrowed credentials are never
    /// written back to disk — the CLI's session file stays the single copy, so
    /// `coder logout` actually revokes local access.
    /// </summary>
    [JsonIgnore] public bool TokenIsExternal { get; private set; }
    [JsonIgnore] public bool UrlIsExternal { get; private set; }

    /// <summary>A configured URL that was rejected, kept only for the error message.</summary>
    [JsonIgnore] public string? RejectedUrl { get; private set; }

    /// <summary>The same for a token we refused to use — see <see cref="Save"/>.</summary>
    [JsonIgnore] public string? RejectedToken { get; private set; }

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoderMascot");

    public static string Path_ => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CoderConfig Load()
    {
        CoderConfig cfg;
        try
        {
            cfg = File.Exists(Path_)
                ? JsonSerializer.Deserialize<CoderConfig>(File.ReadAllText(Path_)) ?? new CoderConfig()
                : new CoderConfig();
        }
        catch
        {
            // A corrupt config must never stop the mascot from starting; it just
            // falls back to CLI/env discovery below.
            cfg = new CoderConfig();
        }

        if (string.IsNullOrWhiteSpace(cfg.Url))
        {
            cfg.Url = Environment.GetEnvironmentVariable("CODER_URL") ?? ReadCliFile("url");
            cfg.UrlIsExternal = true;
        }

        if (string.IsNullOrWhiteSpace(cfg.Token))
        {
            cfg.Token = Environment.GetEnvironmentVariable("CODER_SESSION_TOKEN") ?? ReadCliFile("session");
            cfg.TokenIsExternal = true;
        }

        cfg.Url = cfg.Url?.Trim().TrimEnd('/');
        cfg.Token = cfg.Token?.Trim();

        if (!string.IsNullOrWhiteSpace(cfg.Url) && CleanUrl(cfg.Url) is null)
        {
            cfg.RejectedUrl = cfg.Url;
            cfg.Url = null;
        }

        if (cfg.Token is not null && !IsUsableToken(cfg.Token))
        {
            cfg.RejectedToken = cfg.Token;
            cfg.Token = null;
        }

        // Upper bounds matter as much as lower ones: a hand-edited pollSeconds
        // big enough to overflow TimeSpan would throw inside the poll loop's
        // delay, and the "Edit config…" menu item actively invites the edit.
        cfg.MascotSize = Math.Clamp(cfg.MascotSize, 32, 320);
        cfg.PatrolSpeed = Math.Clamp(cfg.PatrolSpeed, 10, 600);
        cfg.PollSeconds = Math.Clamp(cfg.PollSeconds, 5, 3600);
        cfg.AutoStopWarnMinutes = Math.Clamp(cfg.AutoStopWarnMinutes, 1, 1440);
        cfg.FailuresBeforeAlarm = Math.Clamp(cfg.FailuresBeforeAlarm, 1, 10);
        cfg.SessionPollSeconds = Math.Clamp(cfg.SessionPollSeconds, 10, 3600);
        cfg.SessionStalledSeconds = Math.Clamp(cfg.SessionStalledSeconds, 60, 86400);
        cfg.SessionThinkingStalledSeconds = Math.Clamp(cfg.SessionThinkingStalledSeconds, 30, 86400);

        // Thinking is the impatient case by definition. Configured the other way
        // round it would silently never fire, because a thinking session hits
        // the tool threshold first and is reported as the patient kind.
        cfg.SessionThinkingStalledSeconds =
            Math.Min(cfg.SessionThinkingStalledSeconds, cfg.SessionStalledSeconds);

        // Keep only characters that exist, in order, without duplicates — two
        // windows for one character would sit on top of each other and both
        // write the same saved position. An empty or unrecognised list falls
        // back rather than starting the app with no mascot at all.
        cfg.Characters = (cfg.Characters ?? [])
            .Select(Character.Find)
            .OfType<Character>()
            .Select(c => c.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("pikachu")
            .ToArray();

        cfg.MascotPositions ??= new Dictionary<string, double[]>();

        cfg.Projects = NormalizeProjects(cfg.Projects);
        cfg.CollapsedGroups = ProjectGroups.Live(cfg.CollapsedGroups, cfg.Projects.Select(p => p.Group));

        // A threshold of 0 or 100 is a warning that either never stops or never
        // starts; neither is a setting anyone wants, so keep them out of reach.
        cfg.CpuWarnPercent = Math.Clamp(cfg.CpuWarnPercent, 40, 99);
        cfg.MemoryWarnPercent = Math.Clamp(cfg.MemoryWarnPercent, 40, 99);
        cfg.LoadPollSeconds = Math.Clamp(cfg.LoadPollSeconds, 2, 600);
        cfg.LoadSustainSeconds = Math.Clamp(cfg.LoadSustainSeconds, 0, 86400);

        // 0 means off for both, so only the upper bound is enforced. A negative
        // number would otherwise read as "always idle" and nag continuously.
        cfg.WorkspaceIdleMinutes = cfg.WorkspaceIdleMinutes <= 0
            ? 0 : Math.Clamp(cfg.WorkspaceIdleMinutes, 5, 10080);
        cfg.DevServerIdleMinutes = cfg.DevServerIdleMinutes <= 0
            ? 0 : Math.Clamp(cfg.DevServerIdleMinutes, 5, 10080);

        // Round-trip through the parser so a typo ("quite", "silent") lands on a
        // known value instead of being kept verbatim and read back later as
        // something the switch doesn't recognise.
        cfg.Notifications = AlertGate.Text(AlertGate.Parse(cfg.Notifications));

        // 0 is meaningful — it turns reminders off — so it is not clamped up.
        if (cfg.RemindAfterSeconds != 0)
            cfg.RemindAfterSeconds = Math.Clamp(cfg.RemindAfterSeconds, 15, 86400);
        cfg.RemindMaxSeconds = Math.Clamp(cfg.RemindMaxSeconds, cfg.RemindAfterSeconds, 86400);

        // A one-time lift of the old single-mascot position onto the first
        // character, so upgrading doesn't teleport it back to the default corner.
        if (cfg is { MascotLeft: { } l, MascotTop: { } t } &&
            !cfg.MascotPositions.ContainsKey(cfg.Characters[0]))
            cfg.MascotPositions[cfg.Characters[0]] = [l, t];

        return cfg;
    }

    /// <summary>
    /// The watched-repository list, made usable.
    ///
    /// A project with no path can't be read, so it isn't kept — an unusable row
    /// in the sidebar is a support question, not a feature. The same path twice
    /// is one project: two identical rows would be two views of one repository,
    /// each overwriting the other's reading.
    ///
    /// One definition, called by the loader and by the editor both. Two copies
    /// of this rule is one copy that eventually lets a duplicate through, and
    /// the symptom — a repository whose branches keep changing as you click
    /// between two identical-looking rows — reads as the git code being broken.
    /// </summary>
    public static ProjectEntry[] NormalizeProjects(IEnumerable<ProjectEntry?>? entries)
    {
        ProjectEntry[] kept =
        [.. (entries ?? [])
            .OfType<ProjectEntry>()
            .Where(p => !string.IsNullOrWhiteSpace(p.Path))
            .Select(p => p.Tidy())
            .GroupBy(p => $"{p.Where}:{p.Path}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];

        // One spelling per group, settled here rather than only in the window:
        // typing "backend" today and "Backend" tomorrow files both under one
        // heading, and the file should say so too — otherwise the merge looks
        // like a display trick that the next hand-edit undoes.
        var names = ProjectGroups.Names(kept.Select(p => p.Group));
        foreach (var entry in kept) entry.Group = ProjectGroups.Canonical(entry.Group, names);

        return kept;
    }

    /// <summary>
    /// Replace the watched repositories, through the same sieve as the file.
    ///
    /// Not a plain property set: what the editor hands over has been typed by
    /// hand seconds ago, so it is exactly as likely to hold a blank path or a
    /// duplicate as a file somebody edited in Notepad.
    /// </summary>
    public void SetProjects(IEnumerable<ProjectEntry?>? entries)
    {
        Projects = NormalizeProjects(entries);

        // The group a project just left may have been the last one in it, and a
        // heading nobody can see must not keep a fold that outlives it.
        CollapsedGroups = ProjectGroups.Live(CollapsedGroups, Projects.Select(p => p.Group));
    }

    /// <summary>Remember which headings are folded shut, through the same sieve.</summary>
    public void SetCollapsed(IEnumerable<string?>? keys) =>
        CollapsedGroups = ProjectGroups.Live(keys, Projects.Select(p => p.Group));

    /// <summary>
    /// A Coder URL we are willing to send a session token to, or null.
    ///
    /// The token is bearer-equivalent, so it must never travel in cleartext or
    /// reach a non-http scheme. Embedded userinfo is rejected too, since
    /// "https://coder.example.com@evil.com" reads as trusted at a glance.
    ///
    /// One definition, called by both the file loader and the setup window: two
    /// copies of a credential rule is one copy that eventually gets it wrong.
    /// </summary>
    public static string? CleanUrl(string? url)
    {
        var trimmed = url?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        // Someone pasting a hostname out of the browser bar rarely brings the
        // scheme with it, and refusing that reads as the box being broken.
        //
        // Only a bare host or host:port, though. Anything else carrying a colon
        // already names a scheme, and gluing https:// in front of "ms-msdt:/id"
        // turns a scheme the config loader is tested to reject into one it
        // accepts — a convenience that quietly reopens a hole is not one.
        if (LooksLikeBareHost(trimmed)) trimmed = "https://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)) return null;
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return null;

        var ok = parsed.Scheme == Uri.UriSchemeHttps
                 || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback);

        return ok ? trimmed : null;
    }

    private static bool LooksLikeBareHost(string value)
    {
        if (value.Contains("://", StringComparison.Ordinal)) return false;

        var colon = value.IndexOf(':');
        if (colon < 0) return true;

        // host:port and nothing else — a port is digits, a scheme is not.
        var after = value[(colon + 1)..];
        var end = after.IndexOf('/');
        if (end >= 0) after = after[..end];

        return after.Length > 0 && after.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// A token that can go in an HTTP header at all.
    ///
    /// One with a stray newline or a smart quote in it — which is what pasting
    /// out of a chat window produces — throws inside HttpClient, and at startup
    /// that lands before the tray icon exists: a crash dialog instead of a
    /// mascot.
    /// </summary>
    public static bool IsUsableToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && token.Trim().All(c => c is >= (char)0x21 and <= (char)0x7E);

    /// <summary>The page that shows a session token to copy.</summary>
    public static string? TokenPage(string? url) =>
        CleanUrl(url) is { } clean ? $"{clean}/cli-auth" : null;

    /// <summary>
    /// Nothing to connect with. The app can start, but it has nothing to watch,
    /// so this is what makes the setup window appear.
    /// </summary>
    [JsonIgnore]
    public bool NeedsSetup => string.IsNullOrWhiteSpace(Url) || string.IsNullOrWhiteSpace(Token);

    /// <summary>
    /// Take a URL and token the user supplied themselves.
    ///
    /// This clears the "borrowed" flags on purpose, which is the one thing that
    /// makes <see cref="Save"/> write the token to disk. Everywhere else the
    /// rule is the opposite — a token read from the Coder CLI's session file is
    /// never copied — and it holds because that credential belongs to the CLI.
    /// A token the user pasted into our own window belongs to us: there is no
    /// other copy of it, and refusing to store it would mean asking again on
    /// every launch.
    /// </summary>
    public bool Adopt(string? url, string? token, string? workspace = null)
    {
        if (CleanUrl(url) is not { } cleanUrl) return false;
        if (!IsUsableToken(token)) return false;

        Url = cleanUrl;
        UrlIsExternal = false;
        RejectedUrl = null;

        Token = token!.Trim();
        TokenIsExternal = false;
        RejectedToken = null;

        if (!string.IsNullOrWhiteSpace(workspace)) Workspace = workspace!.Trim();

        return true;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);

            // Strip anything we only borrowed. Without this, dragging the mascot
            // one pixel would persist a second cleartext copy of the CLI's live
            // session token into a file documented as plain settings.
            var onDisk = (CoderConfig)MemberwiseClone();
            if (TokenIsExternal) onDisk.Token = null;
            if (UrlIsExternal) onDisk.Url = null;

            // A value we refused is still something the user typed. Dropping it
            // to null in memory is right — it must not be used — but writing
            // that null back would erase their credential on the first save the
            // app happens to make, and dragging the mascot triggers one.
            if (!UrlIsExternal && onDisk.Url is null) onDisk.Url = RejectedUrl;
            if (!TokenIsExternal && onDisk.Token is null) onDisk.Token = RejectedToken;

            File.WriteAllText(Path_, JsonSerializer.Serialize(onDisk, JsonOpts));
        }
        catch
        {
            // Losing a window position is not worth crashing over.
        }
    }

    /// <summary>Read a file out of the Coder CLI's config dir, if the CLI is set up.</summary>
    private static string? ReadCliFile(string name)
    {
        foreach (var dir in CliConfigDirs())
        {
            try
            {
                var p = System.IO.Path.Combine(dir, name);
                if (File.Exists(p))
                {
                    var v = File.ReadAllText(p).Trim();
                    if (v.Length > 0) return v;
                }
            }
            catch { /* unreadable dir — try the next candidate */ }
        }
        return null;
    }

    private static IEnumerable<string> CliConfigDirs()
    {
        var overrideDir = Environment.GetEnvironmentVariable("CODER_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) yield return overrideDir;

        // Coder uses Go's os.UserConfigDir(), which is %AppData% on Windows.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            yield return System.IO.Path.Combine(appData, "coderv2");

        // Older CLI versions, and anyone who copied a config from Linux/WSL.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return System.IO.Path.Combine(home, ".config", "coderv2");
    }
}
