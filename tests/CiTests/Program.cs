using CoderMascot.Core;

int failures = 0;
void Check(string desc, bool ok)
{
    if (!ok) { failures++; Console.WriteLine($"FAIL: {desc}"); }
}

// --- keeping the repositories current (BranchDiff) ---
{
    var deployed = new Dictionary<string, string> { { "main", "production" } };
    var stamp = DateTimeOffset.Now;

    BranchLine Ref(string name, string sha, string author = "dev", string subject = "work") =>
        new() { Name = name, Sha = sha, Updated = stamp, Author = author, Subject = subject };

    ProjectSnapshot Snap(params BranchLine[] lines) =>
        new() { Name = "app", Path = "/app", Branches = lines, Deployed = deployed };

    var was = Snap(Ref("main", "a1"), Ref("feature/x", "b1"), Ref("gone", "c1"));
    var next = Snap(Ref("main", "a2", "haph", "Fix login"), Ref("feature/x", "b1"), Ref("feature/new", "d1"));
    var changes = BranchDiff.Between(was, next);

    Check("the summary counts them", BranchDiff.Summary(changes) == "1 new · 1 moved · 1 gone");
    Check("a deploy branch moving is news, with who and what",
        BranchDiff.DeployNews("app", changes, deployed) is { } line
        && line.Contains("production (main)") && line.Contains("haph") && line.Contains("Fix login"));
    Check("a feature branch moving is not",
        BranchDiff.DeployNews("app", BranchDiff.Between(was, Snap(Ref("main", "a1"), Ref("feature/x", "b2"))), deployed) is null);
    Check("the first reading compares against nothing", !BranchDiff.Between(null, next).Any);
    Check("nor does a reading that failed", !BranchDiff.Between(was with { Problem = "no" }, next).Any);
    Check("nothing changed is nothing to say", BranchDiff.Summary(BranchDiff.Between(was, was)) is null);
}

// --- pipelines: where they are (CiRemote + the editor) ---
{
    Check("an https remote", CiRemote.Parse("https://git.example.com/team/app.git") == ("git.example.com", "team/app"));
    Check("an scp-style remote", CiRemote.Parse("git@git.example.com:team/sub/app.git") == ("git.example.com", "team/sub/app"));
    Check("an ssh remote with a port", CiRemote.Parse("ssh://git@git.example.com:2222/team/app.git") == ("git.example.com", "team/app"));
    Check("no .git suffix needed", CiRemote.Parse("https://github.com/acme/tool") == ("github.com", "acme/tool"));
    Check("a bare host is not a project", CiRemote.Parse("https://git.example.com/") is null);
    Check("a Windows path is not a remote", CiRemote.Parse(@"C:\src\app") is null);
    Check("nothing is nothing", CiRemote.Parse("  ") is null);

    GitProject Repo(RepoHost host, string path, CiKind ci = CiKind.Auto, string? url = null, string? project = null) =>
        new() { Name = "x", Path = path, Host = host, Ci = ci, CiUrl = url, CiProject = project };

    var gl = CiRemote.Resolve(Repo(RepoHost.Remote, "https://git.example.com/team/app.git"), null);
    Check("a URL project needs no remote read", gl is { Provider: CiProvider.GitLab, Project: "team/app" });
    Check("GitLab's API is under the site", gl!.ApiBase == "https://git.example.com" && gl.Site == "https://git.example.com");
    Check("and the editor can say where", CiRemote.Describe(gl) == "GitLab at git.example.com · team/app");

    var gh = CiRemote.Resolve(Repo(RepoHost.Workspace, "/home/coder/workspace/projects/tool"), "git@github.com:acme/tool.git");
    Check("github.com is GitHub", gh is { Provider: CiProvider.GitHub, Project: "acme/tool" });
    Check("and its API is somewhere else", gh!.ApiBase == "https://api.github.com" && gh.Site == "https://github.com");

    var ghe = CiRemote.Resolve(Repo(RepoHost.Local, "C:/src/tool", CiKind.GitHub), "https://ghe.corp.example/acme/tool.git");
    Check("GitHub named on another host is Enterprise, with the API under it",
        ghe is { Provider: CiProvider.GitHub, ApiBase: "https://ghe.corp.example/api/v3" });

    Check("a checkout with no remote yet has nowhere to ask", CiRemote.Resolve(Repo(RepoHost.Workspace, "/x"), null) is null);
    Check("unless the server and project are given",
        CiRemote.Resolve(Repo(RepoHost.Workspace, "/x", url: "git.example.com", project: "team/app"), null)
            is { Site: "https://git.example.com", Project: "team/app" });
    Check("off is off, whatever the URL says",
        CiRemote.Resolve(Repo(RepoHost.Remote, "https://git.example.com/team/app.git", CiKind.Off), null) is null);
    Check("an explicit path wins over the URL's",
        CiRemote.Resolve(Repo(RepoHost.Remote, "https://git.example.com/team/app.git", project: "other/app"), null)!.Project == "other/app");
    Check("http:// is refused as a server",
        CiRemote.Resolve(Repo(RepoHost.Workspace, "/x", url: "http://git.example.com", project: "a/b"), null) is null);

    var written = new ProjectEntry
    {
        Path = "https://git.example.com/team/app.git", Where = "remote",
        Ci = "GitLab", CiUrl = "git.example.com/", CiProject = "/team/app/",
    }.Tidy();

    Check("ci is spelled one way", written.Ci == "gitlab");
    Check("the server is made a URL", written.CiUrl == "https://git.example.com");
    Check("the project path loses its slashes", written.CiProject == "team/app");
    Check("and reads back", written.ToProject().Ci == CiKind.GitLab && written.ToProject().CiProject == "team/app");
    Check("auto is the default and is not written", new ProjectEntry { Path = "/x", Ci = "  " }.Tidy().Ci is null);
    Check("a config from before pipelines existed is auto", new ProjectEntry { Path = "/x" }.Tidy().ToProject().Ci == CiKind.Auto);
    Check("a server address git would read as a command is dropped, not saved",
        new ProjectEntry { Path = "/x", CiUrl = "ext::sh -c id" }.Tidy().CiUrl is null);

    var edit = ProjectDraft.From(written);
    Check("the editor sees the setting", edit.Ci == CiKind.GitLab && edit.CiUrl == "https://git.example.com");
    edit.Ci = CiKind.Off;
    Check("and writes it back", edit.ToEntry().Ci == "off");
    edit.Ci = CiKind.Auto;
    edit.CiUrl = "http://git.example.com";
    Check("a server address a token must not go to blocks saving", edit.CiProblem is not null);
    edit.CiUrl = string.Empty;
    Check("blank is fine", edit.CiProblem is null);
}

// --- pipelines: what the servers say (CiParse) ---
{
    Check("GitLab's cancelled", CiParse.GitLabState("canceled") == PipelineState.Cancelled);
    Check("GitLab's manual is waiting on someone", CiParse.GitLabState("manual") == PipelineState.Waiting);
    Check("GitLab's queue is pending", CiParse.GitLabState("waiting_for_resource") == PipelineState.Pending);
    Check("a word nobody expected is unknown, not passed", CiParse.GitLabState("bogus") == PipelineState.Unknown);
    Check("GitHub's timed out is a failure", CiParse.GitHubState("completed", "timed_out") == PipelineState.Failed);
    Check("GitHub's queued", CiParse.GitHubState("queued", null) == PipelineState.Pending);
    Check("GitHub's action required is waiting", CiParse.GitHubState("completed", "action_required") == PipelineState.Waiting);
    Check("GitHub's neutral is a pass", CiParse.GitHubState("completed", "neutral") == PipelineState.Passed);
    Check("waiting on a person is neither live nor done", !PipelineState.Waiting.Live() && !PipelineState.Waiting.Done());

    var gitlabList = """
    [
      {"id": 4712, "iid": 32, "project_id": 9, "status": "running", "source": "push", "ref": "feature/x", "sha": "aaa111",
       "web_url": "https://git.example.com/team/app/-/pipelines/4712", "created_at": "2026-09-21T02:10:00.000Z", "updated_at": "2026-09-21T02:11:00.000Z"},
      {"id": 4711, "iid": 31, "project_id": 9, "status": "failed", "source": "push", "ref": "main", "sha": "bbb222",
       "web_url": "https://git.example.com/team/app/-/pipelines/4711", "created_at": "2026-09-21T01:00:00.000Z", "updated_at": "2026-09-21T01:04:30.000Z"},
      {"id": 4710, "status": "success", "ref": "main", "sha": "ccc333", "web_url": "https://git.example.com/team/app/-/pipelines/4710",
       "created_at": "2026-09-20T09:00:00.000Z", "updated_at": "2026-09-20T09:05:00.000Z"},
      {"id": 4709, "status": "manual", "ref": "release", "sha": "ddd444", "web_url": "u", "created_at": "2026-09-19T09:00:00.000Z", "updated_at": "2026-09-19T09:00:00.000Z"},
      {"status": "success", "ref": "orphan"}
    ]
    """;

    var gitlab = CiParse.GitLabPipelines(gitlabList);
    Check("every row with an id, a ref and a link is read", gitlab.Count == 4);
    Check("a row missing those is skipped, not crashed on", gitlab.All(p => p.Branch != "orphan"));
    Check("ids are strings whatever the server sent", gitlab[0].Id == "4712");
    Check("running is running", gitlab[0].State == PipelineState.Running && gitlab[0].Live);
    Check("failed is failed", gitlab[1].State == PipelineState.Failed && gitlab[1].Done);
    Check("manual is waiting", gitlab[3].State == PipelineState.Waiting);
    Check("times are read as UTC", gitlab[1].UpdatedAt == new DateTimeOffset(2026, 9, 21, 1, 4, 30, TimeSpan.Zero));
    Check("the list endpoint knows no title", gitlab[1].Title is null);
    Check("garbage is an empty list, not an exception", CiParse.GitLabPipelines("<html>").Count == 0);

    var gitlabDetail = """
    {"id": 4711, "status": "failed", "duration": 270, "started_at": "2026-09-21T01:00:10.000Z",
     "finished_at": "2026-09-21T01:04:40.000Z", "user": {"name": "Phan Đức", "username": "ducpt"}}
    """;

    var detailed = CiParse.GitLabDetail(gitlabDetail, gitlab[1]);
    Check("the detail adds how long it took", detailed.Duration == TimeSpan.FromSeconds(270));
    Check("and who", detailed.Actor == "Phan Đức");
    Check("and when it ended", detailed.FinishedAt == new DateTimeOffset(2026, 9, 21, 1, 4, 40, TimeSpan.Zero));

    var gitlabJobs = """
    [
      {"id": 9, "name": "unit-tests", "stage": "test", "status": "failed", "failure_reason": "script_failure", "allow_failure": false,
       "duration": 130.5, "web_url": "https://git.example.com/team/app/-/jobs/9",
       "runner": {"id": 3, "description": "gitlab-runner-01", "is_shared": false}, "commit": {"title": "Fix the cap", "author_name": "haph"}},
      {"id": 8, "name": "lint", "stage": "test", "status": "failed", "failure_reason": "script_failure", "allow_failure": true,
       "duration": 10, "runner": null, "commit": {"title": "Fix the cap", "author_name": "haph"}},
      {"id": 7, "name": "compile", "stage": "build", "status": "success", "duration": 60,
       "runner": {"description": "gitlab-runner-01"}, "commit": {"title": "Fix the cap", "author_name": "haph"}}
    ]
    """;

    var jobs = CiParse.GitLabJobs(gitlabJobs);
    Check("every job is read, oldest first", jobs.Jobs.Count == 3 && jobs.Jobs[0].Name == "compile");
    Check("the runner is named", jobs.Jobs[2].Runner == "gitlab-runner-01");
    Check("a runner not yet assigned is none", jobs.Jobs[1].Runner is null);
    Check("the reason is said plainly", jobs.Jobs[2].FailureReason == "the script failed");
    Check("an allowed failure is marked as such", jobs.Jobs[1].AllowFailure && !jobs.Jobs[2].AllowFailure);
    Check("the commit's title and author come with the jobs", jobs.Title == "Fix the cap" && jobs.Author == "haph");
    Check("a runner fault is told apart from a script fault", CiParse.Reason("runner_system_failure") == "the runner itself failed");

    var red = detailed with { Jobs = jobs.Jobs, Title = jobs.Title };
    Check("the job to blame is the one whose failure was not allowed", red.Spotlight?.Name == "unit-tests");
    Check("and the job line says where and why",
        CiWatch.JobLine(red.Spotlight!) == "test › unit-tests on gitlab-runner-01 — the script failed (2m 10s)");

    var githubRuns = """
    {"total_count": 3, "workflow_runs": [
      {"id": 101, "name": "CI", "display_title": "Add pipelines pane", "head_branch": "feature/ci", "head_sha": "eee555",
       "status": "in_progress", "conclusion": null, "event": "push", "html_url": "https://github.com/acme/tool/actions/runs/101",
       "created_at": "2026-09-21T03:00:00Z", "updated_at": "2026-09-21T03:01:00Z", "run_started_at": "2026-09-21T03:00:05Z", "actor": {"login": "ducpt"}},
      {"id": 100, "name": "CI", "display_title": "Release 1.2", "head_branch": "main", "head_sha": "fff666",
       "status": "completed", "conclusion": "failure", "event": "push", "html_url": "https://github.com/acme/tool/actions/runs/100",
       "created_at": "2026-09-20T03:00:00Z", "updated_at": "2026-09-20T03:06:30Z", "run_started_at": "2026-09-20T03:00:30Z", "actor": {"login": "haph"}},
      {"id": 99, "name": "Deploy", "display_title": "Release 1.2", "head_branch": "main", "head_sha": "fff666",
       "status": "completed", "conclusion": "success", "event": "push", "html_url": "u99",
       "created_at": "2026-09-20T03:00:00Z", "updated_at": "2026-09-20T03:08:00Z", "run_started_at": "2026-09-20T03:01:00Z"}
    ]}
    """;

    var github = CiParse.GitHubRuns(githubRuns);
    Check("one row per run", github.Count == 3);
    Check("in progress is running", github[0].State == PipelineState.Running && github[0].Actor == "ducpt");
    Check("a failed conclusion is failed", github[1].State == PipelineState.Failed);
    Check("how long it took is start to last update", github[1].Duration == TimeSpan.FromMinutes(6));
    Check("the workflow's name is kept apart from the title", github[1].Workflow == "CI" && github[1].Title == "Release 1.2");
    Check("a run still going has no length yet", github[0].Duration is null);

    var githubJobs = """
    {"total_count": 1, "jobs": [
      {"id": 5, "name": "build", "status": "completed", "conclusion": "failure", "started_at": "2026-09-20T03:00:40Z",
       "completed_at": "2026-09-20T03:06:00Z", "html_url": "u", "runner_name": "ubuntu-runner-7",
       "steps": [{"name": "Checkout", "status": "completed", "conclusion": "success", "number": 1},
                 {"name": "Run tests", "status": "completed", "conclusion": "failure", "number": 2}]}
    ]}
    """;

    var ghJobs = CiParse.GitHubJobs(githubJobs).Jobs;
    Check("GitHub names its runner too", ghJobs.Count == 1 && ghJobs[0].Runner == "ubuntu-runner-7");
    Check("and the failed step stands in for a reason", ghJobs[0].FailureReason == "step “Run tests”");
    Check("with how long the job took", ghJobs[0].Duration == TimeSpan.FromSeconds(320));
}

// --- pipelines: what they mean (CiWatch) ---
{
    var clock = DateTimeOffset.Now;

    Pipeline Run(string id, string branch, PipelineState state, string sha, int minutesAgo, string? title = null, params PipelineJob[] jobs) =>
        new()
        {
            Id = id, Branch = branch, State = state, Status = state.Word(), Sha = sha, Title = title,
            CreatedAt = clock.AddMinutes(-minutesAgo),
            UpdatedAt = clock.AddMinutes(-minutesAgo + 1),
            FinishedAt = state.Done() ? clock.AddMinutes(-minutesAgo + 1) : null,
            Url = "https://ci/" + id,
            Jobs = jobs,
        };

    var unit = new PipelineJob("unit", "test", PipelineState.Failed, "runner-01", TimeSpan.FromSeconds(90), "the script failed", false, null);

    var sameCommit = new[] { Run("1", "main", PipelineState.Failed, "s1", 10), Run("2", "main", PipelineState.Passed, "s1", 9) };
    Check("the worst run on the newest commit speaks for the branch", CiWatch.LatestOn(sameCommit, "main")?.Id == "1");
    Check("a newer commit's run outranks an older commit's failure",
        CiWatch.LatestOn([.. sameCommit, Run("3", "main", PipelineState.Passed, "s2", 2)], "main")?.Id == "3");
    Check("a branch with no runs has no answer", CiWatch.LatestOn(sameCommit, "dev") is null);
    Check("one row per branch, newest first",
        CiWatch.LatestPerBranch([.. sameCommit, Run("4", "dev", PipelineState.Passed, "d", 1)]).Select(p => p.Branch).SequenceEqual(["dev", "main"]));

    var app = new GitProject
    {
        Name = "app", Path = "/app",
        Deployed = new Dictionary<string, string> { { "main", "production" }, { "staging", "staging" } },
    };

    var reading = new CiSnapshot
    {
        ProjectId = app.Id,
        Pipelines =
        [
            Run("1", "main", PipelineState.Failed, "s1", 10, "Fix the cap", unit),
            Run("2", "feature/x", PipelineState.Failed, "s2", 5),
            Run("3", "staging", PipelineState.Passed, "s3", 20),
        ],
    };

    var broken = CiWatch.Broken([(app, reading)]);
    Check("a red deploy branch is broken", broken.Count == 1 && broken[0].Branch == "main" && broken[0].Environment == "production");
    Check("a red feature branch is that branch's own business", broken.All(b => b.Branch != "feature/x"));
    Check("and the line names the job and the runner", CiWatch.Describe(broken).Contains("unit") && CiWatch.Describe(broken).Contains("runner-01"));
    Check("a reading with a problem breaks nothing", CiWatch.Broken([(app, reading with { Problem = "x" })]).Count == 0);
    Check("nor does a sign-in that is missing", CiWatch.Broken([(app, reading with { NeedsSignIn = true })]).Count == 0);
    Check("a newer green run on the branch clears it",
        CiWatch.Broken([(app, reading with { Pipelines = [.. reading.Pipelines, Run("4", "main", PipelineState.Passed, "s4", 1)] })]).Count == 0);
    Check("a repository with no deploy branches can't be broken",
        CiWatch.Broken([(new GitProject { Name = "x", Path = "/x" }, reading)]).Count == 0);

    var earlier = new CiSnapshot
    {
        ProjectId = app.Id, ReadAt = clock.AddMinutes(-3),
        Pipelines = [Run("10", "main", PipelineState.Running, "a", 4), Run("11", "dev", PipelineState.Passed, "b", 60)],
    };
    var later = new CiSnapshot
    {
        ProjectId = app.Id, ReadAt = clock,
        Pipelines =
        [
            Run("10", "main", PipelineState.Passed, "a", 4) with { UpdatedAt = clock.AddMinutes(-1), FinishedAt = clock.AddMinutes(-1) },
            Run("11", "dev", PipelineState.Passed, "b", 60),
            Run("12", "hotfix", PipelineState.Failed, "c", 2),
            Run("13", "old", PipelineState.Failed, "d", 500),
        ],
    };

    var news = CiWatch.Finished(earlier, later);
    Check("a run that was going and is done is news", news.Any(p => p.Id == "10"));
    Check("one that was already done is not", news.All(p => p.Id != "11"));
    Check("one that appeared and finished since the last look is", news.Any(p => p.Id == "12"));
    Check("one that finished long before the last look is not", news.All(p => p.Id != "13"));
    Check("the first reading announces nothing", CiWatch.Finished(null, later).Count == 0);
    Check("nor does the one after a reading that had failed", CiWatch.Finished(earlier with { Problem = "boom" }, later).Count == 0);

    Check("all announces a pass", CiWatch.Announce("all", Run("1", "main", PipelineState.Passed, "s", 1)));
    Check("failed keeps quiet about a pass", !CiWatch.Announce("failed", Run("1", "main", PipelineState.Passed, "s", 1)));
    Check("...and speaks up about a failure", CiWatch.Announce("failed", Run("1", "main", PipelineState.Failed, "s", 1)));
    Check("off is off", !CiWatch.Announce("off", Run("1", "main", PipelineState.Failed, "s", 1)));
    Check("a typo lands on all", CiWatch.Policy("evrything") == "all");
    Check("a run still going is never announced", !CiWatch.Announce("all", Run("1", "main", PipelineState.Running, "s", 1)));

    var (title, text) = CiWatch.Describe("app", Run("1", "main", PipelineState.Failed, "s", 1, "Fix the cap", unit));
    Check("a failure names the job, the reason and the commit",
        title == "Pipeline failed" && text.Contains("unit") && text.Contains("the script failed") && text.Contains("Fix the cap"));

    var (batchTitle, batchText) = CiWatch.Describe(
        [new PipelineNews("app", Run("1", "main", PipelineState.Failed, "s", 1)), new PipelineNews("api", Run("2", "main", PipelineState.Passed, "t", 1))]);
    Check("several become one line with the counts",
        batchTitle == "2 pipelines finished" && batchText.Contains("1 failed") && batchText.Contains("1 passed") && batchText.Contains("api"));

    Check("a build's length reads as minutes and seconds", CiWatch.Clock(TimeSpan.FromSeconds(252)) == "4m 12s");
    Check("...hours once it is that long", CiWatch.Clock(TimeSpan.FromMinutes(63)) == "1h 03m");
    Check("...and never less than a second", CiWatch.Clock(TimeSpan.Zero) == "1s");

    Check("a running run carries its clock",
        CiWatch.Label(Run("1", "main", PipelineState.Running, "s", 5) with { StartedAt = clock.AddMinutes(-2) }, clock) == "● running · 2m 00s");
    Check("a finished one says when", CiWatch.Label(Run("1", "main", PipelineState.Passed, "s", 30), clock).StartsWith("✓ passed · "));

    Check("off says so", CiWatch.Nothing(null, CiKind.Off, true).Contains("switched off"));
    Check("no sign-in says which server",
        CiWatch.Nothing(new CiSnapshot { ProjectId = "x", NeedsSignIn = true, Target = new CiTarget(CiProvider.GitLab, "https://git.example.com", "https://git.example.com", "t/a") }, CiKind.Auto, true)
            .Contains("git.example.com"));
    Check("polling off names the setting", CiWatch.Nothing(null, CiKind.Auto, false).Contains("ciPollSeconds"));

    Check("a failed pipeline is not an alarm — the sessions are fine", !MascotState.PipelineFailed.IsAlarm());
    Check("but it does bring the mascot over", MascotState.PipelineFailed.NeedsAttention());
}

Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILED");
return failures;
