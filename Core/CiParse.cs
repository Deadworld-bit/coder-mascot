using System.Globalization;
using System.Text.Json;

namespace CoderMascot.Core;

/// <summary>
/// Turning what the GitLab and GitHub APIs actually print into pipelines and
/// jobs. A status mapped to the wrong word is a window confidently reporting a
/// broken build as green, so the state maps are exhaustive and anything neither
/// API taught us is <see cref="PipelineState.Unknown"/>, never a pass. Malformed
/// JSON is an empty list, never an exception — a monitor must not die on a page
/// of HTML from a proxy.
/// </summary>
public static class CiParse
{
    // ---------- status words ----------

    public static PipelineState GitLabState(string? status) => status?.ToLowerInvariant() switch
    {
        "success" => PipelineState.Passed,
        "failed" => PipelineState.Failed,
        "running" => PipelineState.Running,
        "canceled" or "cancelled" => PipelineState.Cancelled,
        "manual" => PipelineState.Waiting,
        "created" or "pending" or "scheduled" or "preparing" or "waiting_for_resource" => PipelineState.Pending,
        "skipped" => PipelineState.Cancelled,
        _ => PipelineState.Unknown,
    };

    public static PipelineState GitHubState(string? status, string? conclusion)
    {
        switch (status?.ToLowerInvariant())
        {
            case "completed":
                return conclusion?.ToLowerInvariant() switch
                {
                    "success" or "neutral" => PipelineState.Passed,
                    "failure" or "timed_out" or "startup_failure" => PipelineState.Failed,
                    "cancelled" or "skipped" or "stale" => PipelineState.Cancelled,
                    "action_required" => PipelineState.Waiting,
                    _ => PipelineState.Unknown,
                };
            case "queued" or "pending" or "requested":
                return PipelineState.Pending;
            case "in_progress":
                return PipelineState.Running;
            case "waiting":
                return PipelineState.Waiting;
            default:
                return PipelineState.Unknown;
        }
    }

    /// <summary>A GitLab failure_reason, said plainly.</summary>
    public static string Reason(string? reason) => reason switch
    {
        "script_failure" => "the script failed",
        "runner_system_failure" => "the runner itself failed",
        "job_execution_timeout" or "stuck_or_timeout_failure" => "it ran out of time",
        "missing_dependency_failure" => "a dependency was missing",
        "runner_unsupported" => "the runner couldn't run it",
        "api_failure" => "the API rejected it",
        null or "" or "unknown_failure" => "it failed",
        _ => reason.Replace('_', ' '),
    };

    // ---------- GitLab ----------

    public static IReadOnlyList<Pipeline> GitLabPipelines(string json)
    {
        var list = new List<Pipeline>();
        foreach (var e in Array(json))
        {
            // A row is only usable with an id, a branch and a link; the API prints
            // thin rows (an orphaned ref, a deleted branch) that carry none.
            if (Id(e) is not { } id || Str(e, "ref") is not { } branch || Str(e, "web_url") is null)
                continue;

            var status = Str(e, "status");
            list.Add(new Pipeline
            {
                Id = id,
                Branch = branch,
                State = GitLabState(status),
                Status = status ?? "",
                Sha = Str(e, "sha"),
                Url = Str(e, "web_url"),
                CreatedAt = Time(e, "created_at") ?? default,
                UpdatedAt = Time(e, "updated_at") ?? default,
            });
        }
        return list;
    }

    /// <summary>Fold a pipeline's detail endpoint onto the list row it belongs to.</summary>
    public static Pipeline GitLabDetail(string json, Pipeline basePipeline)
    {
        if (Object(json) is not { } e) return basePipeline;

        var user = e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : (JsonElement?)null;

        return basePipeline with
        {
            Duration = Seconds(Num(e, "duration")),
            StartedAt = Time(e, "started_at") ?? basePipeline.StartedAt,
            FinishedAt = Time(e, "finished_at") ?? basePipeline.FinishedAt,
            Actor = user is { } uu ? (Str(uu, "name") ?? Str(uu, "username")) : basePipeline.Actor,
        };
    }

    public static JobSet GitLabJobs(string json)
    {
        var jobs = new List<(long Id, PipelineJob Job)>();
        string? title = null, author = null;

        foreach (var e in Array(json))
        {
            if (Str(e, "name") is not { } name) continue;

            if (e.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                title ??= Str(c, "title");
                author ??= Str(c, "author_name");
            }

            var runner = e.TryGetProperty("runner", out var r) && r.ValueKind == JsonValueKind.Object
                ? Str(r, "description")
                : null;

            var state = GitLabState(Str(e, "status"));

            jobs.Add((Long(e, "id") ?? 0, new PipelineJob(
                Name: name,
                Stage: Str(e, "stage") ?? "",
                State: state,
                Runner: runner,
                Duration: Seconds(Num(e, "duration")),
                FailureReason: state == PipelineState.Failed ? Reason(Str(e, "failure_reason")) : null,
                AllowFailure: Bool(e, "allow_failure"),
                Url: Str(e, "web_url"))));
        }

        // Oldest first: the list reads top-to-bottom as the pipeline ran.
        var ordered = jobs.OrderBy(j => j.Id).Select(j => j.Job).ToList();
        return new JobSet(ordered, title, author);
    }

    // ---------- GitHub ----------

    public static IReadOnlyList<Pipeline> GitHubRuns(string json)
    {
        if (Object(json) is not { } root ||
            !root.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<Pipeline>();
        foreach (var e in runs.EnumerateArray())
        {
            if (Id(e) is not { } id || Str(e, "head_branch") is not { } branch) continue;

            var state = GitHubState(Str(e, "status"), Str(e, "conclusion"));
            var started = Time(e, "run_started_at");
            var updated = Time(e, "updated_at");
            var actor = e.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "login") : null;

            list.Add(new Pipeline
            {
                Id = id,
                Branch = branch,
                State = state,
                Status = Str(e, "conclusion") ?? Str(e, "status") ?? "",
                Sha = Str(e, "head_sha"),
                Workflow = Str(e, "name"),
                Title = Str(e, "display_title"),
                Actor = actor,
                Url = Str(e, "html_url"),
                CreatedAt = Time(e, "created_at") ?? default,
                UpdatedAt = updated ?? default,
                StartedAt = started,
                FinishedAt = state.Done() ? updated : null,
                // Length is start to last update, and only once it has stopped.
                Duration = state.Done() && started is { } s && updated is { } up ? up - s : null,
            });
        }
        return list;
    }

    public static JobSet GitHubJobs(string json)
    {
        if (Object(json) is not { } root ||
            !root.TryGetProperty("jobs", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new JobSet([], null, null);

        var jobs = new List<PipelineJob>();
        foreach (var e in arr.EnumerateArray())
        {
            if (Str(e, "name") is not { } name) continue;

            var state = GitHubState(Str(e, "status"), Str(e, "conclusion"));
            var started = Time(e, "started_at");
            var completed = Time(e, "completed_at");

            string? reason = null;
            if (state == PipelineState.Failed && e.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            {
                foreach (var st in steps.EnumerateArray())
                {
                    if (Str(st, "conclusion") is "failure" && Str(st, "name") is { } stepName)
                    {
                        reason = $"step “{stepName}”";
                        break;
                    }
                }
            }

            jobs.Add(new PipelineJob(
                Name: name,
                Stage: "",
                State: state,
                Runner: Str(e, "runner_name"),
                Duration: started is { } s && completed is { } c ? c - s : null,
                FailureReason: reason,
                AllowFailure: false,
                Url: Str(e, "html_url")));
        }

        return new JobSet(jobs, null, null);
    }

    // ---------- JSON helpers ----------

    private static IEnumerable<JsonElement> Array(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { yield break; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in doc.RootElement.EnumerateArray()) yield return e.Clone();
        }
    }

    private static JsonElement? Object(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch { return null; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>An id, as a string, whether the server sent a number or a string.</summary>
    private static string? Id(JsonElement e)
    {
        if (!e.TryGetProperty("id", out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n.ToString(CultureInfo.InvariantCulture) : v.GetRawText(),
            _ => null,
        };
    }

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static TimeSpan? Seconds(double? seconds) =>
        seconds is { } s ? TimeSpan.FromSeconds(s) : null;

    private static DateTimeOffset? Time(JsonElement e, string name)
    {
        if (Str(e, name) is not { } s || s.Length == 0) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
            ? when
            : null;
    }
}
