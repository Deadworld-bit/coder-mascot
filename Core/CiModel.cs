namespace CoderMascot.Core;

/// <summary>
/// The types the pipeline watch is built on: what a repository's CI is, where it
/// lives, and the shape of one pipeline run and its jobs.
///
/// Reconstructed from tests/ConfigTests (the surviving spec) after the 2026-09-21
/// work was lost in the 2026-09-22 workspace re-sync. The behaviour the tests pin
/// — status words, what counts as live/done, which job is to blame — lives in the
/// small extension methods here so the parsers and the watch agree on one answer.
/// </summary>

/// <summary>How a watched repository's pipelines are found.</summary>
public enum CiKind
{
    /// <summary>Work it out from the remote — github.com is GitHub, everything else GitLab.</summary>
    Auto,

    /// <summary>Don't look at pipelines for this repository at all.</summary>
    Off,

    GitLab,
    GitHub,
}

public enum CiProvider { GitLab, GitHub }

/// <summary>
/// Where a run is in its life. Ordered worst-last within each phase so a simple
/// Max picks the state that should speak for a commit — a failure outranks a pass
/// on the same push.
/// </summary>
public enum PipelineState
{
    /// <summary>A word neither API taught us — never reported as a pass.</summary>
    Unknown = 0,

    /// <summary>Queued, not started.</summary>
    Pending = 1,

    /// <summary>Running now.</summary>
    Running = 2,

    /// <summary>Blocked on a person — a manual step or an approval. Not "in flight".</summary>
    Waiting = 3,

    /// <summary>Finished green.</summary>
    Passed = 4,

    /// <summary>Someone cancelled it.</summary>
    Cancelled = 5,

    /// <summary>Finished red.</summary>
    Failed = 6,
}

public static class PipelineStateInfo
{
    /// <summary>In flight — worth watching, not worth announcing.</summary>
    public static bool Live(this PipelineState s) => s is PipelineState.Pending or PipelineState.Running;

    /// <summary>Reached a verdict. Waiting-on-a-person is neither live nor done.</summary>
    public static bool Done(this PipelineState s) =>
        s is PipelineState.Passed or PipelineState.Failed or PipelineState.Cancelled;

    /// <summary>The lower-case word for the state, as the UI and toasts spell it.</summary>
    public static string Word(this PipelineState s) => s switch
    {
        PipelineState.Pending => "pending",
        PipelineState.Running => "running",
        PipelineState.Waiting => "waiting",
        PipelineState.Passed => "passed",
        PipelineState.Cancelled => "cancelled",
        PipelineState.Failed => "failed",
        _ => "unknown",
    };
}

/// <summary>One job (GitLab) or one step-bearing job (GitHub) within a run.</summary>
public sealed record PipelineJob(
    string Name,
    string Stage,
    PipelineState State,
    string? Runner,
    TimeSpan? Duration,
    string? FailureReason,
    bool AllowFailure,
    string? Url);

/// <summary>One pipeline run for one commit on one branch.</summary>
public sealed record Pipeline
{
    public required string Id { get; init; }
    public required string Branch { get; init; }

    public PipelineState State { get; init; } = PipelineState.Unknown;

    /// <summary>The raw word behind <see cref="State"/>, kept for display.</summary>
    public string Status { get; init; } = string.Empty;

    public string? Sha { get; init; }

    /// <summary>The commit subject, when the endpoint carried it.</summary>
    public string? Title { get; init; }

    /// <summary>The workflow's own name (GitHub), kept apart from the commit title.</summary>
    public string? Workflow { get; init; }

    /// <summary>Who set it off.</summary>
    public string? Actor { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    public TimeSpan? Duration { get; init; }
    public string? Url { get; init; }

    public IReadOnlyList<PipelineJob> Jobs { get; init; } = [];

    public bool Live => State.Live();
    public bool Done => State.Done();

    /// <summary>
    /// The job to blame: the first one that failed for real. An allowed failure
    /// is noise — the pipeline is red because of a job that was not permitted to
    /// fail, and pointing at the tolerated one would misname the cause.
    /// </summary>
    public PipelineJob? Spotlight =>
        Jobs.FirstOrDefault(j => j.State == PipelineState.Failed && !j.AllowFailure);
}

/// <summary>A run's jobs, plus the commit facts the job list carries.</summary>
public sealed record JobSet(IReadOnlyList<PipelineJob> Jobs, string? Title, string? Author);

/// <summary>Where a repository's pipelines are read from.</summary>
public sealed record CiTarget(CiProvider Provider, string ApiBase, string Site, string Project);

/// <summary>One reading of a repository's pipelines.</summary>
public sealed record CiSnapshot
{
    public required string ProjectId { get; init; }
    public IReadOnlyList<Pipeline> Pipelines { get; init; } = [];

    /// <summary>Set when the read itself failed — never treated as "all green".</summary>
    public string? Problem { get; init; }

    /// <summary>The server is reachable but wants credentials we don't have.</summary>
    public bool NeedsSignIn { get; init; }

    public DateTimeOffset ReadAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Where this was read from, for the empty-state line.</summary>
    public CiTarget? Target { get; init; }
}

/// <summary>A finished pipeline worth mentioning, tagged with its repository.</summary>
public sealed record PipelineNews(string Project, Pipeline Pipeline);

/// <summary>A deploy branch whose latest run is red.</summary>
public sealed record BrokenDeploy(string Project, string Branch, string Environment, Pipeline Pipeline);
