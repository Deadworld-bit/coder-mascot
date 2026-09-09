using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace CoderMascot.Core;

/// <summary>What a Coder URL and token turned out to be worth.</summary>
public sealed record SetupResult
{
    public required bool Ok { get; init; }

    /// <summary>A sentence to show the user. Empty on success.</summary>
    public string Problem { get; init; } = string.Empty;

    /// <summary>Who the token belongs to, once it is known.</summary>
    public string? Username { get; init; }

    /// <summary>The workspaces that account owns, for picking one.</summary>
    public IReadOnlyList<string> Workspaces { get; init; } = [];
}

/// <summary>
/// Connecting the app for the first time, without a text editor.
///
/// The old first run was: read the README, work out where %APPDATA% is, create a
/// JSON file by hand, and know that "token" means a Coder session token rather
/// than a password. Everyone who was not already a Coder CLI user simply had a
/// mascot that sat there saying Unauthorized.
///
/// So the token is checked *before* it is written. A saved-but-wrong credential
/// is the worst outcome available here: the app looks configured, the badge says
/// Unauthorized, and nothing on screen distinguishes "you pasted the wrong
/// thing" from "the workspace is down" — which is the exact confusion this app
/// exists to remove.
/// </summary>
public static class CoderSetup
{
    /// <summary>Try a URL and token against the real API. Never throws.</summary>
    public static async Task<SetupResult> ProbeAsync(string? url, string? token, CancellationToken ct)
    {
        if (CoderConfig.CleanUrl(url) is not { } clean)
            return Bad("That doesn't look like a Coder address — it needs to start with https://");

        if (!CoderConfig.IsUsableToken(token))
            return Bad(string.IsNullOrWhiteSpace(token)
                ? "Paste the session token from the page that just opened."
                : "That token has characters a token can't contain — copy it again, without any line breaks.");

        // Its own short-lived client: this runs before anything is saved, so it
        // must not touch the live one or the config it polls with.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.Add("User-Agent", "CoderMascot/1.0");

        try
        {
            using var me = await Send(http, clean, token!, "/api/v2/users/me", ct);

            if (me.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Bad("Coder rejected that token. It may have expired — open the page again for a fresh one.");

            if (!me.IsSuccessStatusCode)
                return Bad($"Coder answered HTTP {(int)me.StatusCode}. Check the address is right.");

            var username = await ReadUsername(me, ct);
            return new SetupResult
            {
                Ok = true,
                Username = username,
                Workspaces = await ReadWorkspaces(http, clean, token!, ct),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Bad("Cancelled.");
        }
        catch (TaskCanceledException)
        {
            return Bad("Coder didn't answer in time. Check the address, and whether you need the VPN.");
        }
        catch (HttpRequestException ex)
        {
            // The inner exception is where the useful half lives — "no such
            // host" versus "certificate rejected" are different problems with
            // different fixes, and the outer message says neither.
            var why = ex.InnerException?.Message ?? ex.Message;
            return Bad($"Couldn't reach that address — {why}");
        }
        catch (Exception ex)
        {
            return Bad(ex.Message);
        }
    }

    /// <summary>
    /// Probe, and write it down if it worked.
    ///
    /// Saving is the last step and only happens on a verified credential, so a
    /// failed setup leaves the previous config exactly as it was.
    /// </summary>
    public static async Task<SetupResult> ApplyAsync(
        CoderConfig cfg, string? url, string? token, string? workspace, CancellationToken ct)
    {
        var result = await ProbeAsync(url, token, ct);
        if (!result.Ok) return result;

        // One workspace needs no choosing, and asking would be a question with
        // one answer. Several, and the caller has to say which.
        var chosen = workspace;
        if (string.IsNullOrWhiteSpace(chosen) && result.Workspaces.Count == 1)
            chosen = result.Workspaces[0];

        if (!cfg.Adopt(url, token, chosen))
            return Bad("Couldn't store those details.");

        cfg.Save();
        return result;
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient http, string url, string token, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url + path);
        req.Headers.TryAddWithoutValidation("Coder-Session-Token", token);

        return await http.SendAsync(req, ct);
    }

    private static async Task<string?> ReadUsername(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return doc.RootElement.TryGetProperty("username", out var name) &&
                   name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            // A 200 that isn't the document we expected — a captive portal or a
            // proxy login page. The token still worked as far as we can tell.
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadWorkspaces(
        HttpClient http, string url, string token, CancellationToken ct)
    {
        try
        {
            using var resp = await Send(http, url, token, "/api/v2/workspaces?q=owner:me", ct);
            if (!resp.IsSuccessStatusCode) return [];

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("workspaces", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return [];

            var names = new List<string>();
            foreach (var ws in list.EnumerateArray())
            {
                if (ws.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    name.GetString() is { Length: > 0 } text)
                    names.Add(text);
            }

            return names;
        }
        catch (Exception)
        {
            // Not being able to list workspaces is not a failed setup: the token
            // is good, and the poll can still find the workspace by name later.
            return [];
        }
    }

    private static SetupResult Bad(string problem) => new() { Ok = false, Problem = problem };
}
