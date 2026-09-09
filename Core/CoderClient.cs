using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace CoderMascot.Core;

/// <summary>One poll's worth of truth about the workspace.</summary>
public sealed record WorkspaceSnapshot
{
    public required MascotState State { get; init; }

    /// <summary>Human-readable detail: the reason line shown in the speech bubble.</summary>
    public required string Detail { get; init; }

    public string? WorkspaceName { get; init; }
    public string? BuildStatus { get; init; }
    public string? AgentStatus { get; init; }
    public string? LifecycleState { get; init; }

    /// <summary>Coder's own id for the workspace — needed to start or stop it.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// When Coder last saw this workspace used.
    ///
    /// Coder maintains this itself from agent connections, and drives its own
    /// auto-stop from it — which makes it the one idle signal that already
    /// accounts for code-server, SSH and port-forwards, none of which the mascot
    /// can see from outside.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>When Coder will auto-stop the workspace, if a deadline is set.</summary>
    public DateTimeOffset? Deadline { get; init; }

    public TimeSpan? TimeToDeadline =>
        Deadline is { } d ? d - DateTimeOffset.UtcNow : null;
}

/// <summary>
/// Thin read-only client over the Coder REST API. Only touches endpoints that
/// were verified against the deployment's own CLI binary:
///   GET /api/v2/users/{owner}/workspace/{name}
///   GET /api/v2/workspaces?q=owner:me
/// </summary>
public sealed class CoderClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly CoderConfig _cfg;

    /// <summary>Cached auto-detected workspace name, so we only probe once.</summary>
    private string? _resolvedOwner;
    private string? _resolvedWorkspace;
    private bool _resolveFailed;

    public CoderClient(CoderConfig cfg)
    {
        _cfg = cfg;

        // AllowAutoRedirect must stay off. .NET strips only the Authorization
        // header across a cross-origin redirect, so a custom auth header like
        // Coder-Session-Token would happily follow a 302 to an attacker's host.
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            // Deliberately short: a hung request must not delay detecting a drop
            // past the poll interval.
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "CoderMascot/1.0");
    }

    /// <summary>
    /// Build a request with the token attached per-call, so the credential can
    /// never ride along on a request this code did not construct.
    /// </summary>
    private HttpRequestMessage Authed(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Coder-Session-Token", _cfg.Token);
        return req;
    }

    /// <summary>
    /// Forget what was worked out under the old credentials.
    ///
    /// The resolved workspace and the "couldn't resolve" latch both belong to
    /// the account that was signed in. Keeping them across a re-setup means a
    /// fresh token still polls the previous user's workspace, or gives up
    /// immediately because the *last* account owned several.
    /// </summary>
    public void Forget()
    {
        _resolvedOwner = null;
        _resolvedWorkspace = null;
        _resolveFailed = false;
    }

    public async Task<WorkspaceSnapshot> PollAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cfg.Url))
            return Fail(MascotState.Unauthorized, _cfg.RejectedUrl is { } bad
                ? $"Refusing to use \"{Clamp(bad, 60)}\" — the Coder URL must be https://"
                : "Not connected yet — pick \"Connect to Coder…\" from the tray menu.");

        if (string.IsNullOrWhiteSpace(_cfg.Token))
            return Fail(MascotState.Unauthorized,
                "No session token — pick \"Connect to Coder…\" from the tray menu.");

        try
        {
            var (owner, name) = await ResolveTargetAsync(ct);
            if (name is null)
                return Fail(MascotState.Unauthorized,
                    "You own more than one workspace — pick one in \"Connect to Coder…\".");

            var url = $"{_cfg.Url}/api/v2/users/{Uri.EscapeDataString(owner)}/workspace/{Uri.EscapeDataString(name)}";
            using var req = Authed(url);
            using var resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Fail(MascotState.Unauthorized,
                    "Session expired — \"Connect to Coder…\" in the tray menu, or run `coder login`.");

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return Fail(MascotState.WorkspaceDown, $"Workspace \"{owner}/{name}\" no longer exists.");

            if (!resp.IsSuccessStatusCode)
                return Fail(MascotState.Unreachable, $"Coder returned HTTP {(int)resp.StatusCode}.");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Interpret(doc.RootElement, name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // HttpClient surfaces its own timeout as TaskCanceledException.
            return Fail(MascotState.Unreachable, "Coder did not respond within 10s.");
        }
        catch (HttpRequestException ex)
        {
            return Fail(MascotState.Unreachable, ShortNetworkReason(ex));
        }
        catch (Exception ex)
        {
            return Fail(MascotState.Unreachable, ex.Message);
        }
    }

    /// <summary>
    /// Start or stop the workspace.
    ///
    /// The only write this app performs, and the reason every caller of it puts
    /// a confirmation in front: stopping a workspace kills every session inside
    /// it, which is precisely the disaster the rest of the app exists to warn
    /// about. Returns null on success, or a line to show the user.
    /// </summary>
    public async Task<string?> TransitionAsync(string workspaceId, string transition, CancellationToken ct)
    {
        if (transition is not ("start" or "stop"))
            return $"Unsupported transition \"{transition}\".";

        if (string.IsNullOrWhiteSpace(_cfg.Url) || string.IsNullOrWhiteSpace(_cfg.Token))
            return "Not signed in to Coder.";

        // The id comes from Coder's own response, but it lands in a URL path, so
        // it is checked rather than trusted: anything but a plain uuid is a sign
        // we're not talking to what we think we are.
        if (!Guid.TryParse(workspaceId, out var id))
            return "Coder did not give a usable workspace id.";

        try
        {
            var url = $"{_cfg.Url}/api/v2/workspaces/{id}/builds";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Coder-Session-Token", _cfg.Token);
            req.Content = new StringContent(
                $"{{\"transition\":\"{transition}\"}}", System.Text.Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return null;

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return "Coder rejected that — run `coder login` again.";

            // A build already in flight is the common failure and is not an
            // error worth alarming about.
            if (resp.StatusCode == HttpStatusCode.Conflict)
                return "A build is already running for this workspace.";

            return $"Coder returned HTTP {(int)resp.StatusCode}.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Clamp(ex.Message, 160);
        }
    }

    /// <summary>Work out which workspace to watch, auto-detecting when unambiguous.</summary>
    private async Task<(string Owner, string? Name)> ResolveTargetAsync(CancellationToken ct)
    {
        var owner = string.IsNullOrWhiteSpace(_cfg.Owner) ? "me" : _cfg.Owner!.Trim();

        if (!string.IsNullOrWhiteSpace(_cfg.Workspace))
            return (owner, _cfg.Workspace!.Trim());

        if (_resolvedWorkspace is not null)
            return (_resolvedOwner ?? owner, _resolvedWorkspace);

        // Owning zero or several workspaces can't resolve without a config edit,
        // so don't re-probe forever and double the API traffic.
        if (_resolveFailed) return (owner, null);

        // No workspace configured — if the user owns exactly one, just use it.
        using var req = Authed($"{_cfg.Url}/api/v2/workspaces?q=owner:me");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return (owner, null);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("workspaces", out var list) ||
            list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != 1)
        {
            _resolveFailed = true;
            return (owner, null);
        }

        var ws = list[0];
        _resolvedWorkspace = Str(ws, "name");
        _resolvedOwner = Str(ws, "owner_name") ?? owner;
        return (_resolvedOwner, _resolvedWorkspace);
    }

    /// <summary>
    /// Turn a workspace JSON document into a state + reason, then staple on the
    /// facts every branch needs.
    ///
    /// Split so <see cref="Classify"/> can keep returning early from a dozen
    /// places without every one of them having to remember to carry the id and
    /// the last-used stamp — the kind of omission that is invisible until the
    /// one button that needs it silently does nothing.
    /// </summary>
    private WorkspaceSnapshot Interpret(JsonElement ws, string name) =>
        Classify(ws, name) with
        {
            WorkspaceId = Str(ws, "id"),
            LastUsedAt = ParseTime(ws, "last_used_at"),
        };

    private WorkspaceSnapshot Classify(JsonElement ws, string name)
    {
        var build = ws.TryGetProperty("latest_build", out var b) ? b : default;

        // Anything that isn't a recognisable workspace document — an SSO
        // interstitial, an error envelope, a proxy rewriting the body — must not
        // be reported as "your workspace is destroyed". A false red alarm is the
        // second-worst outcome here, right after a missed one.
        if (build.ValueKind != JsonValueKind.Object || Str(build, "status") is null)
            return new WorkspaceSnapshot
            {
                State = MascotState.Unreachable,
                Detail = "Coder returned an unexpected response.",
                WorkspaceName = name,
            };

        var buildStatus = Clamp(Str(build, "status")!, 40);
        var deadline = ParseDeadline(build);

        // The workspace container itself must be up before the agent can be.
        if (!string.Equals(buildStatus, "running", StringComparison.OrdinalIgnoreCase))
        {
            var state = buildStatus.Equals("starting", StringComparison.OrdinalIgnoreCase)
                        || buildStatus.Equals("pending", StringComparison.OrdinalIgnoreCase)
                ? MascotState.Starting
                : MascotState.WorkspaceDown;

            return new WorkspaceSnapshot
            {
                State = state,
                Detail = state == MascotState.Starting
                    ? $"Workspace is {buildStatus}…"
                    : $"Workspace is {buildStatus} — your Claude sessions are gone.",
                WorkspaceName = name,
                BuildStatus = buildStatus,
                Deadline = deadline,
            };
        }

        var (agentStatus, lifecycle, unhealthyReason) = FirstAgent(build);

        if (agentStatus is null)
            return new WorkspaceSnapshot
            {
                State = MascotState.Starting,
                Detail = "Workspace up, waiting for the agent to register…",
                WorkspaceName = name, BuildStatus = buildStatus, Deadline = deadline,
            };

        // This is the case you actually care about: the box is "running" as far
        // as Coder is concerned, but the agent link is dead, so every session
        // inside it is unreachable.
        if (agentStatus is "disconnected" or "timeout")
            return new WorkspaceSnapshot
            {
                State = MascotState.AgentLost,
                Detail = agentStatus == "timeout"
                    ? "Agent stopped responding — sessions are unreachable."
                    : "Agent disconnected — sessions are unreachable.",
                WorkspaceName = name, BuildStatus = buildStatus,
                AgentStatus = agentStatus, LifecycleState = lifecycle, Deadline = deadline,
            };

        if (agentStatus == "connecting")
            return new WorkspaceSnapshot
            {
                State = MascotState.Starting,
                Detail = "Agent is connecting…",
                WorkspaceName = name, BuildStatus = buildStatus,
                AgentStatus = agentStatus, LifecycleState = lifecycle, Deadline = deadline,
            };

        if (lifecycle is "start_error" or "start_timeout")
            return new WorkspaceSnapshot
            {
                State = MascotState.AgentLost,
                Detail = $"Agent startup failed ({lifecycle}).",
                WorkspaceName = name, BuildStatus = buildStatus,
                AgentStatus = agentStatus, LifecycleState = lifecycle, Deadline = deadline,
            };

        if (lifecycle is "shutting_down" or "shutdown_timeout" or "shutdown_error" or "off")
            return new WorkspaceSnapshot
            {
                State = MascotState.WorkspaceDown,
                Detail = $"Workspace is shutting down ({lifecycle}).",
                WorkspaceName = name, BuildStatus = buildStatus,
                AgentStatus = agentStatus, LifecycleState = lifecycle, Deadline = deadline,
            };

        if (lifecycle is not null && lifecycle != "ready")
            return new WorkspaceSnapshot
            {
                State = MascotState.Starting,
                Detail = $"Agent is {lifecycle.Replace('_', ' ')}…",
                WorkspaceName = name, BuildStatus = buildStatus,
                AgentStatus = agentStatus, LifecycleState = lifecycle, Deadline = deadline,
            };

        if (unhealthyReason is not null)
            return new WorkspaceSnapshot
            {
                State = MascotState.AgentLost,
                Detail = $"Agent unhealthy: {unhealthyReason}",
                WorkspaceName = name, BuildStatus = buildStatus,
                AgentStatus = agentStatus, LifecycleState = lifecycle, Deadline = deadline,
            };

        // Healthy. The only thing left worth warning about is the auto-stop clock.
        var remaining = deadline is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null;
        if (remaining is { } r && r > TimeSpan.Zero &&
            r <= TimeSpan.FromMinutes(_cfg.AutoStopWarnMinutes))
        {
            return new WorkspaceSnapshot
            {
                State = MascotState.AutoStopSoon,
                Detail = $"Coder will auto-stop this workspace in {Humanize(r)}.",
                WorkspaceName = name, BuildStatus = buildStatus,
                AgentStatus = agentStatus, LifecycleState = lifecycle, Deadline = deadline,
            };
        }

        return new WorkspaceSnapshot
        {
            State = MascotState.Connected,
            Detail = remaining is { } ok
                ? $"All good. Auto-stop in {Humanize(ok)}."
                : "All good.",
            WorkspaceName = name, BuildStatus = buildStatus,
            AgentStatus = agentStatus, LifecycleState = lifecycle, Deadline = deadline,
        };
    }

    /// <summary>
    /// Find the first agent across all build resources. A workspace can declare
    /// several resources but only some carry agents.
    /// </summary>
    private static (string? Status, string? Lifecycle, string? UnhealthyReason) FirstAgent(JsonElement build)
    {
        if (build.ValueKind != JsonValueKind.Object ||
            !build.TryGetProperty("resources", out var resources) ||
            resources.ValueKind != JsonValueKind.Array)
            return (null, null, null);

        foreach (var res in resources.EnumerateArray())
        {
            if (!res.TryGetProperty("agents", out var agents) ||
                agents.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var agent in agents.EnumerateArray())
            {
                var status = Str(agent, "status");
                if (status is null) continue;

                string? reason = null;
                if (agent.TryGetProperty("health", out var health) &&
                    health.ValueKind == JsonValueKind.Object &&
                    health.TryGetProperty("healthy", out var healthy) &&
                    healthy.ValueKind == JsonValueKind.False)
                {
                    reason = Clamp(Str(health, "reason") ?? "unknown reason", 200);
                }

                // Every one of these ends up in a 236px speech bubble, so cap
                // them at the source rather than trusting the server's length.
                return (Clamp(status, 40), Clamp(Str(agent, "lifecycle_state"), 40), reason);
            }
        }

        return (null, null, null);
    }

    private static DateTimeOffset? ParseDeadline(JsonElement build) => ParseTime(build, "deadline");

    private static DateTimeOffset? ParseTime(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty(prop, out var d) ||
            d.ValueKind != JsonValueKind.String)
            return null;

        if (!DateTimeOffset.TryParse(d.GetString(), out var parsed)) return null;

        // Coder sends the Go zero time when nothing is scheduled or recorded.
        return parsed.Year <= 1 ? null : parsed.ToUniversalTime();
    }

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Bound a server-supplied string before it reaches the UI.</summary>
    [return: NotNullIfNotNull(nameof(s))]
    private static string? Clamp(string? s, int max) =>
        s is null ? null
        : s.Length > max ? s[..max] + "…"
        : s;

    private static string Humanize(TimeSpan t) =>
        t.TotalMinutes < 1 ? "under a minute"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min"
        : $"{(int)t.TotalHours}h {t.Minutes}m";

    /// <summary>Collapse a nested socket exception into one readable line.</summary>
    private static string ShortNetworkReason(HttpRequestException ex)
    {
        var inner = ex.InnerException?.Message;
        var msg = string.IsNullOrWhiteSpace(inner) ? ex.Message : inner;
        return msg.Length > 120 ? msg[..120] + "…" : msg;
    }

    private static WorkspaceSnapshot Fail(MascotState state, string detail) =>
        new() { State = state, Detail = detail };

    public void Dispose() => _http.Dispose();
}
