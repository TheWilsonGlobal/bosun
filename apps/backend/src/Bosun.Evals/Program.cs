using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bosun.Agent;
using Bosun.Application.Llm;
using Bosun.Domain.Entities;
using Bosun.Infrastructure.Configuration;
using Bosun.Infrastructure.Persistence;
using Bosun.Infrastructure.Seed;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Evals;

/// <summary>Entry point of the eval runner (an explicit type, so it cannot clash with the API's Program in test projects).</summary>
public static class EvalProgram
{
    public static async Task<int> Main(string[] args)
    {
        // dotnet run --project apps/backend/src/Bosun.Evals -- --suite agent [--runs 3] [--case id[,id]] [--no-persist]
        //   --suite extraction [--runs 1] [--fixture id[,id]] [--offline] [--no-persist]   --suite safety [--runs 1] [--case id[,id]] [--offline] [--no-persist]   (all: [--rpm 10] [--quota-retries N])
        // (pnpm run evals -- --suite agent --runs 3)
        string? Arg(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
        }

        // --quota-retries N: tolerate a flapping free-tier quota (a bounded number of extra attempts after a "daily quota" 429)
        if (Arg("--quota-retries") is { } quotaRetries)
        {
            AgentEvalRunner.ExtraSettings["Llm:Resilience:QuotaRetryAttempts"] = quotaRetries;
        }

        // dotnet run --project apps/backend/src/Bosun.Evals -- --generate-fixtures   (pnpm run fixtures:generate)
        if (args.Contains("--generate-fixtures"))
        {
            var root = RepoRoot.Resolve();
            var written = Bosun.Evals.Fixtures.FixtureGenerator.Generate(Path.Combine(root, "evals", "extraction"), Path.Combine(root, "evals", "safety"));
            Console.WriteLine($"Wrote {written.Count} fixture PDFs with ground truth: {string.Join(", ", written)}");
            return 0;
        }

        var suite = Arg("--suite") ?? "agent";
        if (suite == "extraction")
        {
            return await Bosun.Evals.Extraction.ExtractionSuite.RunAsync(args, Arg);
        }

        if (suite == "safety")
        {
            return await Bosun.Evals.Safety.SafetySuite.RunAsync(args, Arg);
        }

        if (suite != "agent")
        {
            Console.Error.WriteLine($"Unknown suite '{suite}'. Implemented: agent, extraction, safety. (rag arrives with its phase.)");
            return 2;
        }

        var runsPerCase = int.Parse(Arg("--runs") ?? "3", CultureInfo.InvariantCulture);
        if (runsPerCase is < 1 or > 20)
        {
            Console.Error.WriteLine("--runs must be between 1 and 20.");
            return 2;
        }

        var only = Arg("--case")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var persist = !args.Contains("--no-persist");
        var repoRoot = RepoRoot.Resolve();
        var casesDirectory = Path.Combine(repoRoot, "evals", "agent");

        var cases = EvalCaseLoader.Load(casesDirectory).Where(c => only is null || only.Contains(c.Id)).ToList();
        if (cases.Count == 0)
        {
            Console.Error.WriteLine(only is null ? $"No cases in {casesDirectory}." : $"No case matches --case {string.Join(",", only)}.");
            return 2;
        }

        if (args.Contains("--list"))
        {
            foreach (var c in cases)
            {
                Console.WriteLine($"{c.Id,-34} {c.Category,-11} {c.Instruction}");
            }

            return 0;
        }

        try
        {
            AgentEvalRunner.RequireLlmConfiguration();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var work = Path.Combine(Path.GetTempPath(), "bosun-evals", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        var seedFile = Path.Combine(work, "bosun.seed.db");
        Console.WriteLine($"Seeding {Path.GetFileName(seedFile)} ...");
        await SeedDatabase.CreateAsync(seedFile);

        var rpm = int.Parse(Arg("--rpm") ?? "10", CultureInfo.InvariantCulture);
        var runner = new AgentEvalRunner(seedFile, Path.Combine(work, "runs"), new LlmThrottle(rpm));
        var started = DateTime.UtcNow;
        var attempts = new List<AttemptResult>();
        var total = cases.Count * runsPerCase;
        Console.WriteLine($"Suite agent: {cases.Count} case(s) x {runsPerCase} run(s) = {total} attempt(s). Every attempt starts from a fresh copy of the seeded database. LLM requests are paced at {rpm}/min (--rpm).");
        Console.WriteLine();

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            foreach (var evalCase in cases)
            {
                for (var attempt = 1; attempt <= runsPerCase; attempt++)
                {
                    var result = await runner.RunAsync(evalCase, attempt, cancel.Token);
                    attempts.Add(result);
                    Console.WriteLine(
                        $"[{attempts.Count,3}/{total}] {evalCase.Id,-34} #{attempt} {(result.Passed ? "PASS" : "FAIL")} {result.Outcome,-22} {result.LatencyMs / 1000.0,5:F1}s {result.TotalTokens,7:N0} tok {result.ToolCalls,2} calls"
                        + (result.InfrastructureRetries > 0 ? $" (repeated {result.InfrastructureRetries}x after provider errors)" : "")
                        + (result.Passed || result.Detail is null ? "" : $"  <- {result.Detail}"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cancelled: reporting what finished.");
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // temp directory outside the repo
            }
        }

        if (attempts.Count == 0)
        {
            return 1;
        }

        var finished = DateTime.UtcNow;
        var model = runner.Model;
        var promptVersion = runner.PromptVersion;
        var perCase = cases
            .Select(c => (Case: c, Attempts: attempts.Where(a => a.CaseId == c.Id).ToList()))
            .Where(x => x.Attempts.Count > 0)
            .ToList();

        // ---------- table ----------
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"Agent eval  model={model}  prompt={promptVersion}");
        sb.AppendLine(new string('-', 118));
        sb.AppendLine($"{"case",-34} {"category",-11} {"pass",-6} {"rate",5}  {"outcomes",-36} {"avg tok",8} {"avg s",6}");
        sb.AppendLine(new string('-', 118));
        foreach (var (c, list) in perCase)
        {
            var passed = list.Count(a => a.Passed);
            var outcomes = string.Join(", ", list.GroupBy(a => a.Outcome).OrderByDescending(g => g.Count()).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
            sb.AppendLine($"{c.Id,-34} {c.Category,-11} {passed + "/" + list.Count,-6} {100.0 * passed / list.Count,4:F0}%  {Clip(outcomes, 36),-36} {list.Average(a => a.TotalTokens),8:N0} {list.Average(a => a.LatencyMs) / 1000.0,6:F1}");
        }

        var passedAll = attempts.Count(a => a.Passed);
        var rate = (double)passedAll / attempts.Count;
        sb.AppendLine(new string('-', 118));
        sb.AppendLine($"{"OVERALL",-34} {"",-11} {passedAll + "/" + attempts.Count,-6} {100 * rate,4:F1}%  (target >= 85%)  cases at 100%: {perCase.Count(x => x.Attempts.All(a => a.Passed))}/{perCase.Count}");
        foreach (var group in attempts.GroupBy(a => a.Category).OrderBy(g => g.Key))
        {
            sb.AppendLine($"  {group.Key,-30} {group.Count(a => a.Passed)}/{group.Count()}");
        }

        Console.Write(sb);

        // ---------- tokens and cost (the same table as /api/ai/usage) ----------
        var usageRows = attempts.SelectMany(a => a.Usage)
            .GroupBy(r => (r.Feature, r.Model, r.Kind))
            .Select(g => new LlmUsageRow(
                g.Key.Feature, g.Key.Model, g.Key.Kind, g.Sum(r => r.Calls), g.Sum(r => r.Failures), g.Sum(r => r.PromptTokens), g.Sum(r => r.CandidateTokens),
                g.Sum(r => r.ThoughtTokens), g.Sum(r => r.TotalTokens), g.Sum(r => r.AvgLatencyMs * r.Calls) / Math.Max(1, g.Sum(r => r.Calls)), g.Max(r => r.MaxLatencyMs), g.Sum(r => r.CostUsd)))
            .OrderBy(r => r.Feature, StringComparer.Ordinal)
            .ToList();
        Console.WriteLine();
        Console.Write(LlmUsageReport.Format(usageRows));
        var totals = LlmUsageReport.Sum(usageRows);

        // ---------- results file (no secrets: ids, outcomes, numbers, the agent's closing message) ----------
        var resultsDirectory = Path.Combine(repoRoot, "evals", "results");
        Directory.CreateDirectory(resultsDirectory);
        var stamp = started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var resultsPath = Path.Combine(resultsDirectory, $"agent-{stamp}.json");
        var report = new
        {
            suite,
            startedAt = started,
            finishedAt = finished,
            model,
            promptVersion,
            runsPerCase,
            summary = new
            {
                cases = perCase.Count,
                attempts = attempts.Count,
                passed = passedAll,
                passRate = Math.Round(rate, 4),
                casesAt100Percent = perCase.Count(x => x.Attempts.All(a => a.Passed)),
                promptTokens = attempts.Sum(a => a.PromptTokens),
                candidateTokens = attempts.Sum(a => a.CandidateTokens),
                thoughtTokens = attempts.Sum(a => a.ThoughtTokens),
                totalTokens = attempts.Sum(a => a.TotalTokens),
                estimatedCostUsd = Math.Round(totals.CostUsd, 6),
                llmCalls = totals.Calls,
                llmFailures = totals.Failures,
                avgToolCalls = Math.Round(attempts.Average(a => a.ToolCalls), 2),
                avgLatencyMs = (int)attempts.Average(a => a.LatencyMs),
            },
            cases = perCase.Select(x => new
            {
                id = x.Case.Id,
                category = x.Case.Category,
                instruction = x.Case.Instruction,
                passed = x.Attempts.Count(a => a.Passed),
                attempts = x.Attempts.Count,
                passRate = Math.Round((double)x.Attempts.Count(a => a.Passed) / x.Attempts.Count, 4),
                runs = x.Attempts.Select(a => new
                {
                    attempt = a.Attempt,
                    passed = a.Passed,
                    outcome = a.Outcome,
                    detail = a.Detail,
                    status = a.Status,
                    errorCode = a.ErrorCode,
                    toolCalls = a.ToolCalls,
                    tokens = a.TotalTokens,
                    thoughtTokens = a.ThoughtTokens,
                    latencyMs = a.LatencyMs,
                    agentMessage = a.Message,
                    netEffect = a.Effects,
                }),
            }),
            usage = usageRows.Select(r => new { r.Feature, r.Model, r.Calls, r.Failures, r.PromptTokens, r.CandidateTokens, r.ThoughtTokens, r.CostUsd }),
        };
        await File.WriteAllTextAsync(
            resultsPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n");
        Console.WriteLine($"Results: {Path.GetRelativePath(repoRoot, resultsPath)}");

        // ---------- EvalRun / EvalResult rows (admin console graphs them) ----------
        if (persist)
        {
            var devConfiguration = new ConfigurationBuilder().AddBosunEnv().Build();
            var devDatabase = new SqliteConnectionStringBuilder(devConfiguration["Database:ConnectionString"] ?? throw new InvalidOperationException("DB_CONNECTION is not configured.")).DataSource;
            await using var provider = AgentEvalRunner.BuildProvider(devDatabase, withLlm: false);
            await DatabaseInitializer.MigrateAsync(provider);
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
            var row = new EvalRun
            {
                Suite = suite,
                StartedAt = started,
                FinishedAt = finished,
                Model = model,
                PromptVersion = promptVersion,
                RunsPerCase = runsPerCase,
                Cases = perCase.Count,
                Attempts = attempts.Count,
                Passed = passedAll,
                PassRate = rate,
                PromptTokens = attempts.Sum(a => a.PromptTokens),
                CandidateTokens = attempts.Sum(a => a.CandidateTokens),
                ThoughtTokens = attempts.Sum(a => a.ThoughtTokens),
                CostUsd = totals.CostUsd,
                ResultsFile = Path.GetRelativePath(repoRoot, resultsPath).Replace('\\', '/'),
                Results = attempts.Select(a => new EvalResult
                {
                    CaseId = a.CaseId,
                    Category = a.Category,
                    Attempt = a.Attempt,
                    Passed = a.Passed,
                    Outcome = a.Outcome,
                    Detail = a.Detail is { Length: > 2000 } d ? d[..2000] : a.Detail,
                    AgentStatus = a.Status,
                    ErrorCode = a.ErrorCode,
                    ToolCalls = a.ToolCalls,
                    Tokens = a.TotalTokens,
                    LatencyMs = a.LatencyMs,
                }).ToList(),
            };
            db.EvalRuns.Add(row);
            await db.SaveChangesAsync();
            Console.WriteLine($"Saved eval_runs #{row.Id} with {row.Results.Count} eval_results to the configured database.");
        }

        return 0;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
