using System.Net;
using System.Net.Http;

namespace CoderMascot.Core;

/// <summary>
/// Reads a repository's pipelines from GitLab or GitHub.
///
/// A plain read-only REST client: it lists recent pipelines for a project and,
/// for the newest failed one, its jobs (so a "Pipeline failed" line can name the
/// job that broke). It gets the token from the supplied provider — which reads
/// git's own credential store — and puts it on the one request that goes to that
/// host, never on the shared client, so no host's token can ride a request to
/// another. The API base is https by construction (see <see cref="CiRemote"/>).
/// A 401/403 becomes "needs sign-in", not an error; anything else is a Problem,
/// and neither is ever mistaken for "all green".
/// </summary>
public sealed class CiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<CiTarget, CancellationToken, Task<string?>> _tokenFor;
    private readonly bool _ownsHttp;

    public CiClient(Func<CiTarget, CancellationToken, Task<string?>> tokenFor, HttpClient? http = null)
    {
        _tokenFor = tokenFor;
        _ownsHttp = http is null;

        // No auto-redirect, the same rule CoderClient holds: .NET strips the
        // typed Authorization header across a cross-origin redirect but NOT a
        // custom one like PRIVATE-TOKEN, so a compromised or misconfigured host
        // that answers with a 302 could otherwise carry the token elsewhere. A
        // 3xx is then a non-success status and becomes a Problem below.
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        // GitHub rejects a request with no User-Agent; this one carries no token.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CoderMascot");
    }

    public async Task<CiSnapshot> ReadAsync(GitProject project, CiTarget target, CancellationToken ct)
    {
        try
        {
            var token = await _tokenFor(target, ct).ConfigureAwait(false);

            return target.Provider == CiProvider.GitLab
                ? await ReadGitLab(project, target, token, ct).ConfigureAwait(false)
                : await ReadGitHub(project, target, token, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CiSnapshot { ProjectId = project.Id, Target = target, Problem = Short(ex.Message) };
        }
    }

    // ---------- GitLab ----------

    private async Task<CiSnapshot> ReadGitLab(GitProject project, CiTarget target, string? token, CancellationToken ct)
    {
        var proj = Uri.EscapeDataString(target.Project);
        var auth = token is null ? default((string, string)?) : ("PRIVATE-TOKEN", token);

        var (body, status) = await Get($"{target.ApiBase}/api/v4/projects/{proj}/pipelines?per_page=20", auth, ct);
        if (NeedsSignIn(status)) return SignIn(project, target);
        if (body is null) return Failed(project, target, "GitLab", status);

        var pipelines = CiParse.GitLabPipelines(body);

        if (Newest(pipelines, PipelineState.Failed) is { } red && IsId(red.Id))
        {
            var (jobsBody, _) = await Get($"{target.ApiBase}/api/v4/projects/{proj}/pipelines/{red.Id}/jobs?per_page=100", auth, ct);
            if (jobsBody is not null)
            {
                var jobs = CiParse.GitLabJobs(jobsBody);
                pipelines = Replace(pipelines, red with { Jobs = jobs.Jobs, Title = red.Title ?? jobs.Title });
            }
        }

        return new CiSnapshot { ProjectId = project.Id, Target = target, Pipelines = pipelines };
    }

    // ---------- GitHub ----------

    private async Task<CiSnapshot> ReadGitHub(GitProject project, CiTarget target, string? token, CancellationToken ct)
    {
        var auth = token is null ? default((string, string)?) : ("Authorization", $"Bearer {token}");

        // Encode each path segment. Resolve already validates the project shape,
        // so this is belt-and-suspenders against anything odd reaching the URL —
        // GitLab escapes the whole id (:59); GitHub keeps the slashes.
        var repo = string.Join('/', target.Project.Split('/').Select(Uri.EscapeDataString));

        var (body, status) = await Get($"{target.ApiBase}/repos/{repo}/actions/runs?per_page=20", auth, ct);
        if (NeedsSignIn(status)) return SignIn(project, target);
        if (body is null) return Failed(project, target, "GitHub", status);

        var pipelines = CiParse.GitHubRuns(body);

        if (Newest(pipelines, PipelineState.Failed) is { } red && IsId(red.Id))
        {
            var (jobsBody, _) = await Get($"{target.ApiBase}/repos/{repo}/actions/runs/{red.Id}/jobs?per_page=100", auth, ct);
            if (jobsBody is not null)
            {
                var jobs = CiParse.GitHubJobs(jobsBody);
                pipelines = Replace(pipelines, red with { Jobs = jobs.Jobs });
            }
        }

        return new CiSnapshot { ProjectId = project.Id, Target = target, Pipelines = pipelines };
    }

    // ---------- plumbing ----------

    private async Task<(string? Body, HttpStatusCode Status)> Get(string url, (string Name, string Value)? auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // Per request, never on the shared client: the token belongs to this host
        // only, and DefaultRequestHeaders would carry it to the next one too.
        if (auth is { } a) req.Headers.TryAddWithoutValidation(a.Name, a.Value);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return (null, resp.StatusCode);

        return (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), resp.StatusCode);
    }

    private static bool NeedsSignIn(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static CiSnapshot SignIn(GitProject project, CiTarget target) =>
        new() { ProjectId = project.Id, Target = target, NeedsSignIn = true };

    private static CiSnapshot Failed(GitProject project, CiTarget target, string provider, HttpStatusCode status) =>
        new() { ProjectId = project.Id, Target = target, Problem = $"{provider} returned {(int)status}." };

    private static Pipeline? Newest(IReadOnlyList<Pipeline> pipelines, PipelineState state) =>
        pipelines.Where(p => p.State == state).OrderByDescending(p => p.CreatedAt).FirstOrDefault();

    /// <summary>A server-issued id safe to drop into a URL — digits only.</summary>
    private static bool IsId(string id) => id.Length > 0 && id.All(char.IsAsciiDigit);

    private static IReadOnlyList<Pipeline> Replace(IReadOnlyList<Pipeline> pipelines, Pipeline updated) =>
        pipelines.Select(p => p.Id == updated.Id ? updated : p).ToList();

    private static string Short(string message)
    {
        var clean = message.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return clean.Length > 160 ? clean[..160] + "…" : clean;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
