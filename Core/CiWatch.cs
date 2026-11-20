namespace CoderMascot.Core;

/// <summary>
/// What the pipeline readings mean: which run speaks for a branch, when a deploy
/// branch counts as broken, what finished since the last look, and how to say any
/// of it in one line.
///
/// Each of these has a way of being quietly wrong — a deploy run hidden behind a
/// build run on the same commit, a red feature branch parking the mascot, a first
/// reading announcing yesterday's news — so the rules live here, once, tested.
/// </summary>
public static class CiWatch
{
    /// <summary>
    /// The run that speaks for a branch: the worst run on its newest commit.
    ///
    /// Newest commit, not newest run, so a build that passed after a deploy failed
    /// on the same push doesn't paint the branch green; and worst-on-that-commit,
    /// so a later green commit does clear an earlier red one.
    /// </summary>
    public static Pipeline? LatestOn(IEnumerable<Pipeline> pipelines, string branch)
    {
        var forBranch = pipelines.Where(p => p.Branch == branch).ToList();
        if (forBranch.Count == 0) return null;

        var newestSha = forBranch.OrderByDescending(p => p.CreatedAt).First().Sha;

        return forBranch
            .Where(p => p.Sha == newestSha)
            .OrderByDescending(p => (int)p.State)          // worst state wins (enum is worst-last)
            .ThenByDescending(p => p.CreatedAt)
            .First();
    }

    /// <summary>One run per branch, the branch with the most recent activity first.</summary>
    public static IReadOnlyList<Pipeline> LatestPerBranch(IEnumerable<Pipeline> pipelines)
    {
        var all = pipelines.ToList();
        var newest = all.GroupBy(p => p.Branch)
            .ToDictionary(g => g.Key, g => g.Max(p => p.CreatedAt));

        return all.Select(p => p.Branch).Distinct()
            .Select(b => LatestOn(all, b)!)
            .OrderByDescending(p => newest[p.Branch])
            .ToList();
    }

    /// <summary>Deploy branches whose latest run is red. Feature branches are their own business.</summary>
    public static IReadOnlyList<BrokenDeploy> Broken(IEnumerable<(GitProject Project, CiSnapshot Reading)> readings)
    {
        var broken = new List<BrokenDeploy>();

        foreach (var (project, reading) in readings)
        {
            // A read that failed, or one we couldn't sign in for, tells us nothing —
            // reporting either as "broken" would cry wolf on a network blip.
            if (reading.Problem is not null || reading.NeedsSignIn) continue;

            foreach (var (branch, environment) in project.Deployed)
            {
                if (LatestOn(reading.Pipelines, branch) is { State: PipelineState.Failed } red)
                    broken.Add(new BrokenDeploy(project.Name, branch, environment, red));
            }
        }

        return broken;
    }

    /// <summary>A human line for the broken deploys, naming the job and where it ran.</summary>
    public static string Describe(IReadOnlyList<BrokenDeploy> broken) =>
        string.Join("\n", broken.Select(b =>
        {
            var detail = b.Pipeline.Spotlight is { } job ? JobLine(job) : b.Pipeline.State.Word();
            return $"{b.Environment} ({b.Branch}) — {detail}";
        }));

    /// <summary>
    /// Runs that reached a verdict since the last look — and only those.
    ///
    /// Not the first reading (everything would be "new"), not a reading that
    /// failed (its absence of a run is not the run finishing), not a run that was
    /// already done last time, and not one that finished before we last looked.
    /// </summary>
    public static IReadOnlyList<Pipeline> Finished(CiSnapshot? earlier, CiSnapshot later)
    {
        if (earlier is null || earlier.Problem is not null) return [];

        // Indexed by id, last-wins: a server that repeats an id in one response
        // must not throw here and take the poll loop down with it.
        var before = new Dictionary<string, Pipeline>();
        foreach (var p in earlier.Pipelines) before[p.Id] = p;

        return later.Pipelines.Where(p =>
                p.Done
                && (!before.TryGetValue(p.Id, out var was) || !was.Done)
                && (p.FinishedAt is null || p.FinishedAt > earlier.ReadAt))
            .ToList();
    }

    /// <summary>Whether a finished run is worth a word under the given policy.</summary>
    public static bool Announce(string? policy, Pipeline pipeline)
    {
        if (!pipeline.Done) return false;

        return Policy(policy) switch
        {
            "off" => false,
            "failed" => pipeline.State == PipelineState.Failed,
            _ => true,
        };
    }

    /// <summary>Fold any spelling of the announce policy onto a known word.</summary>
    public static string Policy(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" or "none" or "silent" => "off",
        "failed" or "failures" or "failure" or "fail" => "failed",
        _ => "all",
    };

    /// <summary>One finished pipeline, as a toast.</summary>
    public static (string Title, string Text) Describe(string project, Pipeline pipeline)
    {
        var title = pipeline.State switch
        {
            PipelineState.Failed => "Pipeline failed",
            PipelineState.Passed => "Pipeline passed",
            PipelineState.Cancelled => "Pipeline cancelled",
            _ => $"Pipeline {pipeline.State.Word()}",
        };

        var text = $"{project} · {pipeline.Branch}";
        if (pipeline.Title is { Length: > 0 } commit) text += $" — {commit}";
        if (pipeline.Spotlight is { } job) text += $"\n{JobLine(job)}";

        return (title, text);
    }

    /// <summary>Several finished pipelines, as one toast with the counts.</summary>
    public static (string Title, string Text) Describe(IReadOnlyList<PipelineNews> news)
    {
        var failed = news.Count(n => n.Pipeline.State == PipelineState.Failed);
        var passed = news.Count(n => n.Pipeline.State == PipelineState.Passed);
        var other = news.Count - failed - passed;

        var counts = new List<string>();
        if (failed > 0) counts.Add($"{failed} failed");
        if (passed > 0) counts.Add($"{passed} passed");
        if (other > 0) counts.Add($"{other} other");

        var who = string.Join(", ", news.Select(n => $"{n.Project} ({n.Pipeline.Branch})"));

        return ($"{news.Count} pipelines finished", $"{string.Join(" · ", counts)}\n{who}");
    }

    /// <summary>How long a build took, as m/s or h/m, never less than a second.</summary>
    public static string Clock(TimeSpan span)
    {
        var s = Math.Max(1, (int)Math.Round(span.TotalSeconds));
        if (s < 60) return $"{s}s";
        if (s < 3600) return $"{s / 60}m {s % 60:00}s";
        return $"{s / 3600}h {s % 3600 / 60:00}m";
    }

    /// <summary>A one-line status badge for a run, with its clock.</summary>
    public static string Label(Pipeline pipeline, DateTimeOffset now)
    {
        if (pipeline.Live)
        {
            var elapsed = pipeline.StartedAt is { } s ? now - s : TimeSpan.Zero;
            return $"● {pipeline.State.Word()} · {Clock(elapsed)}";
        }

        var glyph = pipeline.State switch
        {
            PipelineState.Passed => "✓",
            PipelineState.Failed => "✗",
            PipelineState.Cancelled => "⊘",
            _ => "•",
        };

        var when = pipeline.FinishedAt ?? pipeline.UpdatedAt;
        return $"{glyph} {pipeline.State.Word()} · {Friendly.Since(when)}";
    }

    /// <summary>Why there is nothing to show, in words that point at the fix.</summary>
    public static string Nothing(CiSnapshot? reading, CiKind kind, bool pollingOn)
    {
        if (kind == CiKind.Off)
            return "Pipelines are switched off for this repository.";

        if (reading is { NeedsSignIn: true })
        {
            var host = reading.Target is { } t && Uri.TryCreate(t.Site, UriKind.Absolute, out var u) ? u.Host : "the server";
            return $"Sign in to {host} to read its pipelines.";
        }

        if (!pollingOn)
            return "Pipeline polling is off — set ciPollSeconds above zero to turn it on.";

        return "No pipelines read yet.";
    }

    /// <summary>"stage › job on runner — reason (clock)", with each part only if known.</summary>
    public static string JobLine(PipelineJob job)
    {
        var line = string.IsNullOrEmpty(job.Stage) ? job.Name : $"{job.Stage} › {job.Name}";
        if (job.Runner is { Length: > 0 } runner) line += $" on {runner}";
        if (job.FailureReason is { Length: > 0 } reason) line += $" — {reason}";
        if (job.Duration is { } d) line += $" ({Clock(d)})";
        return line;
    }
}
