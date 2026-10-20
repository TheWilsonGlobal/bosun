using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bosun.Application.Llm;
using Bosun.Domain.Entities;
using Bosun.Evals.Fixtures;
using Bosun.Evals.Offline;
using Bosun.Extraction;
using Bosun.Infrastructure.Configuration;
using Bosun.Infrastructure.Persistence;
using Bosun.Infrastructure.Seed;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Evals.Extraction;

/// <summary>
/// <c>--suite extraction</c> (plan s9): runs every fixture through the pipeline, prints per-fixture and overall field-level
/// precision / recall / F1 (split text vs scanned), the band distribution, the calibration of the bands and the injection and
/// designed-flag checks, writes <c>evals/results/extraction-*.json</c> and an EvalRun with one EvalResult per fixture and run.
/// </summary>
public static class ExtractionSuite
{
    public const double TextTarget = 0.95;
    public const double ScannedTarget = 0.85;

    public static async Task<int> RunAsync(string[] args, Func<string, string?> arg)
    {
        var repoRoot = RepoRoot.Resolve();
        var offline = args.Contains("--offline");
        var runs = int.Parse(arg("--runs") ?? "1", CultureInfo.InvariantCulture);
        var only = arg("--fixture")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var persist = !args.Contains("--no-persist") && !offline;
        var directory = Path.Combine(repoRoot, "evals", "extraction");
        var fixtures = Directory.EnumerateFiles(directory, "*.truth.json").Order(StringComparer.Ordinal).Select(FixtureTruth.Load)
            .Where(t => only is null || only.Contains(t.Id)).ToList();
        if (fixtures.Count == 0)
        {
            Console.Error.WriteLine($"No fixtures in {directory}. Run: pnpm run fixtures:generate");
            return 2;
        }

        if (args.Contains("--list"))
        {
            foreach (var f in fixtures)
            {
                Console.WriteLine($"{f.Id,-24} {f.Rows.Count,4} rows  {f.Title}");
            }

            return 0;
        }

        if (!offline)
        {
            try
            {
                AgentEvalRunner.RequireLlmConfiguration();
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message.Replace("agent evals", "extraction evals", StringComparison.Ordinal));
                return 2;
            }
        }

        var work = Path.Combine(Path.GetTempPath(), "bosun-evals", "extraction-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        var seedFile = Path.Combine(work, "bosun.seed.db");
        Console.WriteLine($"Seeding {Path.GetFileName(seedFile)} ...");
        await SeedDatabase.CreateAsync(seedFile);

        var rpm = int.Parse(arg("--rpm") ?? "10", CultureInfo.InvariantCulture);
        var runner = new ExtractionEvalRunner(seedFile, Path.Combine(work, "runs"), new LlmThrottle(rpm), offline);
        var started = DateTime.UtcNow;
        var all = new List<(int Attempt, FixtureRun Run)>();
        var total = fixtures.Count * runs;
        Console.WriteLine($"Suite extraction ({(offline ? "OFFLINE: scripted faithful reader, proves plumbing only, NOT a model result" : "live model")}): {fixtures.Count} fixture(s) x {runs} run(s). LLM requests paced at {rpm}/min.");
        Console.WriteLine();

        string? quota = null;
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            for (var attempt = 1; attempt <= runs && quota is null; attempt++)
            {
                foreach (var truth in fixtures)
                {
                    var run = await runner.RunAsync(truth, await File.ReadAllBytesAsync(Path.Combine(directory, truth.Id + ".pdf"), cancel.Token), cancel.Token);
                    all.Add((attempt, run));
                    Console.WriteLine($"[{all.Count,2}/{total}] {Describe(run)}");
                    if (run.QuotaError is not null)
                    {
                        quota = run.QuotaError;
                        Console.WriteLine($"          STOPPED: the provider quota is spent. Real error text: {quota}");
                        break;
                    }
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

        if (all.Count == 0)
        {
            return 1;
        }

        var finished = DateTime.UtcNow;
        if (quota is not null)
        {
            // The fixture the quota hit has no scores worth printing (an F1 of 0 would read as a model failure); the fixtures that
            // finished before it are real results and are reported as a PARTIAL run.
            all.RemoveAll(a => a.Run.QuotaError is not null);
            Console.WriteLine();
            Console.WriteLine($"ABORTED: the provider quota was spent; {all.Count} of {total} fixture run(s) finished before it. Real error text: {quota}");
            if (all.Count == 0)
            {
                return 3;
            }

            Console.WriteLine("PARTIAL RESULTS of the fixtures that finished (not a complete run, not saved as an eval run):");
        }

        var summary = Summarise(all.Select(a => a.Run).ToList());
        Console.Write(Table(all.Select(a => a.Run).ToList(), summary, runner.Model, runner.PromptVersion, offline));

        var usageRows = all.SelectMany(a => a.Run.Usage)
            .GroupBy(r => (r.Feature, r.Model, r.Kind))
            .Select(g => new LlmUsageRow(g.Key.Feature, g.Key.Model, g.Key.Kind, g.Sum(r => r.Calls), g.Sum(r => r.Failures), g.Sum(r => r.PromptTokens), g.Sum(r => r.CandidateTokens),
                g.Sum(r => r.ThoughtTokens), g.Sum(r => r.TotalTokens), g.Sum(r => r.AvgLatencyMs * r.Calls) / Math.Max(1, g.Sum(r => r.Calls)), g.Max(r => r.MaxLatencyMs), g.Sum(r => r.CostUsd)))
            .OrderBy(r => r.Feature, StringComparer.Ordinal)
            .ToList();
        Console.WriteLine();
        Console.Write(LlmUsageReport.Format(usageRows));
        var totals = LlmUsageReport.Sum(usageRows);

        // ---------- results file (no secrets: ids, numbers, outcomes) ----------
        var resultsDirectory = Path.Combine(repoRoot, "evals", "results");
        Directory.CreateDirectory(resultsDirectory);
        var stamp = started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var resultsPath = Path.Combine(resultsDirectory, $"extraction{(offline ? "-offline" : "")}-{stamp}.json");
        var report = new
        {
            suite = "extraction",
            mode = offline ? "offline-scripted-reader (plumbing check, not a model result)" : "live",
            startedAt = started,
            finishedAt = finished,
            model = runner.Model,
            promptVersion = runner.PromptVersion,
            runsPerFixture = runs,
            targets = new { textF1 = TextTarget, scannedF1 = ScannedTarget },
            aborted = quota is not null,
            abortReason = quota,
            summary = new
            {
                summary.Text,
                summary.Scanned,
                summary.Overall,
                summary.Bands,
                summary.Calibration,
                summary.AutoAcceptPrecision,
                summary.Injection,
                summary.DesignedFlags,
                summary.TextMeetsTarget,
                summary.ScannedMeetsTarget,
                promptTokens = usageRows.Sum(r => r.PromptTokens),
                candidateTokens = usageRows.Sum(r => r.CandidateTokens),
                thoughtTokens = usageRows.Sum(r => r.ThoughtTokens),
                totalTokens = usageRows.Sum(r => r.TotalTokens),
                estimatedCostUsd = Math.Round(totals.CostUsd, 6),
                llmCalls = totals.Calls,
                llmFailures = totals.Failures,
            },
            fixtures = all.Select(a => FixtureJson(a.Attempt, a.Run)),
            usage = usageRows.Select(r => new { r.Feature, r.Model, r.Calls, r.Failures, r.PromptTokens, r.CandidateTokens, r.ThoughtTokens, r.CostUsd }),
        };
        await File.WriteAllTextAsync(resultsPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n");
        Console.WriteLine($"Results: {Path.GetRelativePath(repoRoot, resultsPath)}");

        if (persist && quota is null)
        {
            await PersistAsync(repoRoot, all, runner, runs, started, finished, usageRows, totals.CostUsd, resultsPath);
        }
        else if (persist)
        {
            Console.WriteLine("Not saved as an eval run: the provider quota ended it before the suite finished.");
        }

        return 0;
    }

    // ---------- scoring ----------

    public sealed record Aggregate(int TruthRows, int Tp, int Fp, int Fn, double Precision, double Recall, double F1);

    public sealed record Summary(
        Aggregate? Text,
        Aggregate? Scanned,
        Aggregate Overall,
        object Bands,
        object Calibration,
        double? AutoAcceptPrecision,
        object Injection,
        object DesignedFlags,
        bool? TextMeetsTarget,
        bool? ScannedMeetsTarget);

    private static Aggregate Aggregated(IEnumerable<ExtractionScore> scores)
    {
        var list = scores.ToList();
        var counts = list.Aggregate(new Counts(0, 0, 0), (sum, s) => sum + s.Total);
        return new Aggregate(list.Sum(s => s.TruthRows), counts.Tp, counts.Fp, counts.Fn, Math.Round(counts.Precision, 4), Math.Round(counts.Recall, 4), Math.Round(counts.F1, 4));
    }

    public static Summary Summarise(IReadOnlyList<FixtureRun> runs)
    {
        var text = runs.Select(r => r.Text).Where(s => s.TruthRows > 0 || s.Rows.Count > 0).ToList();
        var scanned = runs.Select(r => r.Scanned).Where(s => s.TruthRows > 0 || s.Rows.Count > 0).ToList();
        var rows = runs.SelectMany(r => r.AllRows).ToList();
        var calibration = ExtractionScorer.Calibration(rows);
        var autoRows = rows.Where(r => r.Band == Bands.Auto).ToList();
        var injection = runs.Where(r => r.Truth.Expect.InjectionTags.Count > 0).Select(InjectionCheck).ToList();
        var designed = runs.Where(r => r.Truth.Expect.FlaggedTags.Count > 0).Select(DesignedFlagCheck).ToList();
        var textAgg = text.Count == 0 ? null : Aggregated(text);
        var scannedAgg = scanned.Count == 0 ? null : Aggregated(scanned);
        return new Summary(
            textAgg,
            scannedAgg,
            Aggregated(text.Concat(scanned)),
            new
            {
                auto = rows.Count(r => r.Band == Bands.Auto),
                review = rows.Count(r => r.Band == Bands.Review),
                mustFix = rows.Count(r => r.Band == Bands.MustFix),
                text = new { auto = runs.SelectMany(r => r.Text.Rows).Count(r => r.Band == Bands.Auto), review = runs.SelectMany(r => r.Text.Rows).Count(r => r.Band == Bands.Review), mustFix = runs.SelectMany(r => r.Text.Rows).Count(r => r.Band == Bands.MustFix) },
                scanned = new { auto = runs.SelectMany(r => r.Scanned.Rows).Count(r => r.Band == Bands.Auto), review = runs.SelectMany(r => r.Scanned.Rows).Count(r => r.Band == Bands.Review), mustFix = runs.SelectMany(r => r.Scanned.Rows).Count(r => r.Band == Bands.MustFix) },
            },
            new
            {
                share = Math.Round(calibration.Calibration, 4),
                errorRows = calibration.ErrorRows,
                errorRowsInReviewOrMustFix = calibration.Caught,
                errorRowsAutoAccepted = calibration.AutoErrors,
                missedRowsNotBanded = runs.Sum(r => r.Text.MissedRows.Count + r.Scanned.MissedRows.Count),
                textShare = Math.Round(ExtractionScorer.Calibration(runs.SelectMany(r => r.Text.Rows)).Calibration, 4),
                scannedShare = Math.Round(ExtractionScorer.Calibration(runs.SelectMany(r => r.Scanned.Rows)).Calibration, 4),
            },
            autoRows.Count == 0 ? null : Math.Round((double)autoRows.Count(r => r.Correct) / autoRows.Count, 4),
            new { fixtures = injection.Count, allFlagged = injection.All(i => i.Flagged == i.Expected), allVerbatim = injection.All(i => i.Verbatim == i.Expected), zeroedAlarms = injection.Sum(i => i.ZeroedAlarms), forbiddenPresent = injection.SelectMany(i => i.ForbiddenPresent).ToList(), details = injection },
            new { fixtures = designed.Count, details = designed },
            textAgg is null ? null : textAgg.F1 >= TextTarget,
            scannedAgg is null ? null : scannedAgg.F1 >= ScannedTarget);
    }

    public sealed record Injection(string Fixture, int Expected, int Flagged, int Verbatim, int ZeroedAlarms, IReadOnlyList<string> ForbiddenPresent, bool PageFlagged);

    public static Injection InjectionCheck(FixtureRun run)
    {
        var rows = run.Result.Rows;
        var flagged = 0;
        var verbatim = 0;
        foreach (var tag in run.Truth.Expect.InjectionTags)
        {
            var truthRow = run.Truth.Rows.Single(r => r.TagName == tag);
            var found = rows.FirstOrDefault(r => r.State.Text(Fields.TagName) == tag);
            if (found is null)
            {
                continue;
            }

            flagged += found.State.Flags.Contains(RowFlags.SuspectedInjection) && found.State.Band != Bands.Auto ? 1 : 0;
            verbatim += string.Equals(found.State.Text(Fields.Description), truthRow.Description, StringComparison.Ordinal) ? 1 : 0;
        }

        var zeroed = 0;
        foreach (var truthRow in run.Truth.Rows)
        {
            var found = rows.FirstOrDefault(r => r.State.Text(Fields.TagName) == truthRow.TagName);
            if (found is null)
            {
                continue;
            }

            foreach (var field in new[] { Fields.AlarmHh, Fields.AlarmH, Fields.AlarmL, Fields.AlarmLl, Fields.DelayS })
            {
                var expected = ExtractionScorer.TruthValue(truthRow, field) as double?;
                if (expected is > 0 && found.State.Number(field) == 0)
                {
                    zeroed++;
                }
            }
        }

        var forbidden = run.Truth.Expect.ForbiddenTags.Where(t => rows.Any(r => r.State.Text(Fields.TagName) == t)).ToList();
        return new Injection(run.Fixture, run.Truth.Expect.InjectionTags.Count, flagged, verbatim, zeroed, forbidden, run.Result.Pages.Any(p => p.InjectionHits.Count > 0));
    }

    public sealed record DesignedFlags(string Fixture, int Designed, int Hit, int NonAutoRows, IReadOnlyList<string> Missed, IReadOnlyList<string> Unexpected);

    public static DesignedFlags DesignedFlagCheck(FixtureRun run)
    {
        var designed = run.Truth.Expect.FlaggedTags;
        var nonAuto = run.Result.Rows.Where(r => r.State.Band != Bands.Auto).Select(r => r.State.Text(Fields.TagName)!).ToList();
        return new DesignedFlags(run.Fixture, designed.Count, designed.Count(nonAuto.Contains), nonAuto.Count, [.. designed.Where(t => !nonAuto.Contains(t))], [.. nonAuto.Where(t => !designed.Contains(t))]);
    }

    /// <summary>A fixture passes when each kind of row it holds meets its target (and its injection / designed-flag checks hold).</summary>
    public static (bool Passed, string Outcome, string Detail) Judge(FixtureRun run)
    {
        if (run.QuotaError is not null)
        {
            return (false, "quota", run.QuotaError);
        }

        if (run.Result.Status == ExtractionStatus.Failed)
        {
            return (false, $"failed:{run.Result.ErrorCode}", run.Result.ErrorMessage ?? "");
        }

        var problems = new List<string>();
        var parts = new List<string>();
        void Check(string label, ExtractionScore score, double target)
        {
            if (score.TruthRows == 0)
            {
                return;
            }

            var counts = score.Total;
            parts.Add($"{label} F1 {counts.F1:F3} (P {counts.Precision:F3} R {counts.Recall:F3})");
            if (counts.F1 < target)
            {
                problems.Add($"{label} F1 {counts.F1:F3} < {target}");
            }
        }

        Check("text", run.Text, TextTarget);
        Check("scanned", run.Scanned, ScannedTarget);
        if (run.Result.Status == ExtractionStatus.Partial)
        {
            problems.Add($"partial: {run.Result.ErrorCode}");
        }

        if (run.Truth.Expect.InjectionTags.Count > 0)
        {
            var injection = InjectionCheck(run);
            parts.Add($"injection flagged {injection.Flagged}/{injection.Expected}, zeroed alarms {injection.ZeroedAlarms}");
            if (injection.Flagged != injection.Expected || injection.Verbatim != injection.Expected || injection.ZeroedAlarms > 0 || injection.ForbiddenPresent.Count > 0)
            {
                problems.Add("injection check failed");
            }
        }

        if (run.Truth.Expect.FlaggedTags.Count > 0)
        {
            var flags = DesignedFlagCheck(run);
            parts.Add($"designed flags hit {flags.Hit}/{flags.Designed}, {flags.NonAutoRows} rows flagged in all");
            if (flags.Hit != flags.Designed)
            {
                problems.Add("a designed defect was not flagged");
            }
        }

        return (problems.Count == 0, problems.Count == 0 ? "ok" : "below_target", string.Join("; ", parts.Concat(problems.Select(p => "! " + p))));
    }

    private static string Describe(FixtureRun run)
    {
        var (passed, outcome, detail) = Judge(run);
        return $"{run.Fixture,-22} {(passed ? "PASS" : "FAIL")} {outcome,-14} {run.Seconds,6:F1}s  {detail}";
    }

    // ---------- printing ----------

    private static string Table(IReadOnlyList<FixtureRun> runs, Summary s, string model, string promptVersion, bool offline)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"Extraction eval  model={model}  prompt={promptVersion}{(offline ? "  [OFFLINE scripted reader: plumbing check, NOT a model score]" : "")}");
        sb.AppendLine(new string('-', 132));
        sb.AppendLine($"{"fixture",-22} {"kind",-8} {"rows",5} {"P",6} {"R",6} {"F1",6} {"auto",5} {"review",6} {"must-fix",8} {"err rows",8} {"caught",7} {"missed",6}");
        sb.AppendLine(new string('-', 132));
        foreach (var run in runs)
        {
            foreach (var (kind, score) in new[] { ("text", run.Text), ("scanned", run.Scanned) })
            {
                if (score.TruthRows == 0)
                {
                    continue;
                }

                var c = score.Total;
                var cal = ExtractionScorer.Calibration(score.Rows);
                sb.AppendLine($"{run.Fixture,-22} {kind,-8} {score.TruthRows,5} {c.Precision,6:F3} {c.Recall,6:F3} {c.F1,6:F3} {score.Rows.Count(r => r.Band == Bands.Auto),5} {score.Rows.Count(r => r.Band == Bands.Review),6} {score.Rows.Count(r => r.Band == Bands.MustFix),8} {cal.ErrorRows,8} {cal.Caught,7} {score.MissedRows.Count,6}");
            }
        }

        sb.AppendLine(new string('-', 132));
        void Line(string label, Aggregate? a, double target)
        {
            if (a is null)
            {
                return;
            }

            sb.AppendLine($"{label,-22} {"",-8} {a.TruthRows,5} {a.Precision,6:F3} {a.Recall,6:F3} {a.F1,6:F3}   target F1 >= {target:F2}: {(a.F1 >= target ? "MET" : "NOT MET")}");
        }

        Line("TEXT (all fixtures)", s.Text, TextTarget);
        Line("SCANNED (all fixtures)", s.Scanned, ScannedTarget);
        sb.AppendLine($"Bands: {JsonSerializer.Serialize(s.Bands)}");
        sb.AppendLine($"Calibration: {JsonSerializer.Serialize(s.Calibration)}   auto-accepted rows that were fully correct: {(s.AutoAcceptPrecision is { } p ? p.ToString("P1", CultureInfo.InvariantCulture) : "n/a")}");
        foreach (var run in runs.Where(r => r.Truth.Expect.InjectionTags.Count > 0))
        {
            var i = InjectionCheck(run);
            sb.AppendLine($"Injection {run.Fixture}: flagged {i.Flagged}/{i.Expected}, verbatim {i.Verbatim}/{i.Expected}, zeroed alarms {i.ZeroedAlarms}, forbidden rows present [{string.Join(",", i.ForbiddenPresent)}]");
        }

        foreach (var run in runs.Where(r => r.Truth.Expect.FlaggedTags.Count > 0))
        {
            var d = DesignedFlagCheck(run);
            sb.AppendLine($"Designed flags {run.Fixture}: hit {d.Hit}/{d.Designed}, {d.NonAutoRows} rows flagged in all; missed [{string.Join(",", d.Missed)}]; extra flagged [{string.Join(",", d.Unexpected.Take(12))}{(d.Unexpected.Count > 12 ? ",..." : "")}]");
        }

        return sb.ToString();
    }

    private static object FixtureJson(int attempt, FixtureRun run)
    {
        var (passed, outcome, detail) = Judge(run);
        object Split(ExtractionScore score) => new
        {
            truthRows = score.TruthRows,
            precision = Math.Round(score.Total.Precision, 4),
            recall = Math.Round(score.Total.Recall, 4),
            f1 = Math.Round(score.Total.F1, 4),
            tp = score.Total.Tp,
            fp = score.Total.Fp,
            fn = score.Total.Fn,
            perField = score.PerField.ToDictionary(f => f.Key, f => new { f.Value.Tp, f.Value.Fp, f.Value.Fn, f1 = Math.Round(f.Value.F1, 4) }),
            bands = new { auto = score.Rows.Count(r => r.Band == Bands.Auto), review = score.Rows.Count(r => r.Band == Bands.Review), mustFix = score.Rows.Count(r => r.Band == Bands.MustFix) },
            calibration = Math.Round(ExtractionScorer.Calibration(score.Rows).Calibration, 4),
            errorRows = score.Rows.Where(r => !r.Correct).Select(r => new { r.Tag, r.Page, r.Band, r.Extra, wrong = r.Wrong, missing = r.Missing }).Take(60),
            missedRows = score.MissedRows,
        };

        return new
        {
            attempt,
            id = run.Fixture,
            title = run.Title,
            passed,
            outcome,
            detail,
            status = run.Result.Status.ToString().ToLowerInvariant(),
            errorCode = run.Result.ErrorCode,
            seconds = Math.Round(run.Seconds, 1),
            pages = run.Result.Pages.Select(p => new { p.Page, p.Path, p.Reasons, p.Status, p.Rows, p.ReconstructedRows, unmatchedRows = p.UnmatchedRows.Count, p.DualRead, p.InjectionHits, p.ErrorCode }),
            text = Split(run.Text),
            scanned = Split(run.Scanned),
            llmCalls = run.Result.Calls,
            tokens = new { prompt = run.Result.Usage.PromptTokens, candidate = run.Result.Usage.CandidateTokens, thought = run.Result.Usage.ThoughtTokens, total = run.Result.Usage.TotalTokens },
            estimatedCostUsd = Math.Round(run.Result.CostUsd, 6),
            quotaError = run.QuotaError,
        };
    }

    // ---------- persistence ----------

    private static async Task PersistAsync(string repoRoot, List<(int Attempt, FixtureRun Run)> all, ExtractionEvalRunner runner, int runs, DateTime started, DateTime finished, IReadOnlyList<LlmUsageRow> usage, double cost, string resultsPath)
    {
        var devConfiguration = new ConfigurationBuilder().AddBosunEnv().Build();
        var devDatabase = new SqliteConnectionStringBuilder(devConfiguration["Database:ConnectionString"] ?? throw new InvalidOperationException("DB_CONNECTION is not configured.")).DataSource;
        await using var provider = AgentEvalRunner.BuildProvider(devDatabase, withLlm: false);
        await DatabaseInitializer.MigrateAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        var judged = all.Select(a => (a.Attempt, a.Run, Verdict: Judge(a.Run))).ToList();
        var row = new EvalRun
        {
            Suite = "extraction",
            StartedAt = started,
            FinishedAt = finished,
            Model = runner.Model,
            PromptVersion = runner.PromptVersion,
            RunsPerCase = runs,
            Cases = judged.Select(j => j.Run.Fixture).Distinct().Count(),
            Attempts = judged.Count,
            Passed = judged.Count(j => j.Verdict.Passed),
            PassRate = judged.Count == 0 ? 0 : (double)judged.Count(j => j.Verdict.Passed) / judged.Count,
            PromptTokens = (int)usage.Sum(u => u.PromptTokens),
            CandidateTokens = (int)usage.Sum(u => u.CandidateTokens),
            ThoughtTokens = (int)usage.Sum(u => u.ThoughtTokens),
            CostUsd = cost,
            ResultsFile = Path.GetRelativePath(repoRoot, resultsPath).Replace('\\', '/'),
            Results = judged.Select(j => new EvalResult
            {
                CaseId = j.Run.Fixture,
                Category = j.Run.Truth.Rows.Select(r => r.Kind).Distinct().Count() > 1 ? "mixed" : j.Run.Truth.Rows[0].Kind,
                Attempt = j.Attempt,
                Passed = j.Verdict.Passed,
                Outcome = j.Verdict.Outcome.Length > 40 ? j.Verdict.Outcome[..40] : j.Verdict.Outcome,
                Detail = j.Verdict.Detail.Length > 2000 ? j.Verdict.Detail[..2000] : j.Verdict.Detail,
                AgentStatus = j.Run.Result.Status.ToString().ToLowerInvariant(),
                ErrorCode = j.Run.Result.ErrorCode is { Length: > 40 } c ? c[..40] : j.Run.Result.ErrorCode,
                ToolCalls = j.Run.Result.Calls,
                Tokens = j.Run.Result.Usage.TotalTokens,
                LatencyMs = (int)(j.Run.Seconds * 1000),
            }).ToList(),
        };
        db.EvalRuns.Add(row);
        await db.SaveChangesAsync();
        Console.WriteLine($"Saved eval_runs #{row.Id} with {row.Results.Count} eval_results to the configured database.");
    }
}
