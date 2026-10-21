using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bosun.Agent;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Application.Rag;
using Bosun.Domain.Entities;
using Bosun.Infrastructure.Configuration;
using Bosun.Infrastructure.Persistence;
using Bosun.Infrastructure.Seed;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bosun.Evals.Rag;

public sealed class RagExpectation
{
    public string Source { get; set; } = "";

    public string Section { get; set; } = "";
}

public sealed class RagCase
{
    public string Id { get; set; } = "";

    /// <summary>answerable | unanswerable</summary>
    public string Category { get; set; } = "";

    public string Question { get; set; } = "";

    public RagExpectation? Expect { get; set; }

    public string? Notes { get; set; }

    public bool Answerable => Category == "answerable";

    public static IReadOnlyList<RagCase> Load(string path)
    {
        var cases = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build()
            .Deserialize<List<RagCase>>(File.ReadAllText(path));
        foreach (var c in cases)
        {
            if (c.Category is not ("answerable" or "unanswerable") || string.IsNullOrWhiteSpace(c.Question) || (c.Answerable && c.Expect is null))
            {
                throw new InvalidDataException($"RAG case '{c.Id}' is incomplete (category answerable|unanswerable, a question, and `expect` when answerable).");
            }
        }

        if (cases.Select(c => c.Id).Distinct().Count() != cases.Count)
        {
            throw new InvalidDataException("RAG case ids must be unique.");
        }

        return cases;
    }
}

/// <param name="Rank">1-based position of the expected section in the fused top-k; null when absent (or the case is unanswerable).</param>
public sealed record RagAttempt(
    RagCase Case,
    bool Passed,
    string Outcome,
    string? Detail,
    bool Answered,
    string? RefusalReason,
    int? Rank,
    bool? ExpectedAboveThreshold,
    double? TopCosine,
    IReadOnlyList<string> Retrieved,
    IReadOnlyList<string> Cited,
    string Answer,
    string? ErrorCode,
    int Tokens,
    int ThoughtTokens,
    int LatencyMs);

/// <summary>
/// The rag eval (plan s9): 14 answerable and 6 unanswerable questions against the real retrieval (real embeddings, FTS5, fusion,
/// threshold) and the real model. Retrieval is deterministic, so every case runs ONCE. Two groups of numbers are reported separately:
/// retrieval only (hit@k, MRR of the expected section in the fused top-k) and the answer (cited the right section, refusals).
/// </summary>
public static class RagSuite
{
    public const double Target = 0.90;

    /// <summary>Unanswerable questions are gated on their own: a confident answer to a question the documents cannot answer is the worst failure of a RAG assistant, so 100 % of them must be refused (the overall rate cannot hide one).</summary>
    public const double UnanswerableTarget = 1.0;

    public static (bool Overall, bool Unanswerable) Gates(int passed, int total, int refused, int unanswerable) =>
        (total > 0 && (double)passed / total >= Target, unanswerable == 0 || (double)refused / unanswerable >= UnanswerableTarget);

    public static async Task<int> RunAsync(string[] args, Func<string, string?> arg)
    {
        var repoRoot = RepoRoot.Resolve();
        var retrievalOnly = args.Contains("--retrieval-only");
        var persist = !args.Contains("--no-persist") && !retrievalOnly;
        var only = arg("--case")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var cases = RagCase.Load(Path.Combine(repoRoot, "evals", "rag", "cases.yaml")).Where(c => only is null || only.Contains(c.Id)).ToList();
        if (cases.Count == 0)
        {
            Console.Error.WriteLine("No rag cases selected.");
            return 2;
        }

        if (args.Contains("--list"))
        {
            foreach (var c in cases)
            {
                Console.WriteLine($"{c.Id,-32} {c.Category,-12} {c.Question}");
            }

            return 0;
        }

        var configuration = new ConfigurationBuilder().AddBosunEnv().Build();
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(configuration["Embedding:ApiKey"]))
        {
            missing.Add("GEMINI_EMBEDDING_API_KEY");
        }

        if (!retrievalOnly && (string.IsNullOrWhiteSpace(configuration["Llm:ApiKey"]) || string.IsNullOrWhiteSpace(configuration["Llm:Model"])))
        {
            missing.Add("GEMINI_LLM_API_KEY / LLM_MODEL");
        }

        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"The rag eval calls the real embedding{(retrievalOnly ? "" : " and LLM")} API. Set {string.Join(" and ", missing)} in the root .env.");
            return 2;
        }

        var work = Path.Combine(Path.GetTempPath(), "bosun-evals", "rag-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        var seedFile = Path.Combine(work, "bosun.seed.db");
        await SeedDatabase.CreateAsync(seedFile);
        var dbPath = SeedDatabase.CopyTo(seedFile, Path.Combine(work, "run"));
        var rpm = int.Parse(arg("--rpm") ?? "10", CultureInfo.InvariantCulture);
        var started = DateTime.UtcNow;
        var attempts = new List<RagAttempt>();
        IndexReport index;
        string model;
        string embeddingModel;
        string promptVersion;
        IReadOnlyList<LlmUsageRow> usageRows;

        try
        {
            await using var provider = AgentEvalRunner.BuildProvider(dbPath, withLlm: true, throttle: new LlmThrottle(rpm));
            await using var scope = provider.CreateAsyncScope();
            model = scope.ServiceProvider.GetRequiredService<ILlmClient>().DefaultModel;
            embeddingModel = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>().Model;
            promptVersion = scope.ServiceProvider.GetRequiredService<PromptLibrary>().Get("rag.ask.system").Version;

            Console.WriteLine($"Suite rag{(retrievalOnly ? " (retrieval only: no LLM calls)" : "")}: {cases.Count} case(s), each run once. Indexing seed/docs into a temp database copy (embeddings come from the cache file when present) ...");
            try
            {
                index = await scope.ServiceProvider.GetRequiredService<KnowledgeIngestService>().ReindexAsync(1, cacheOnly: false, CancellationToken.None);
            }
            catch (Exception ex) when (ex is LlmException or AppException)
            {
                Console.Error.WriteLine($"Indexing failed: {(ex as LlmException)?.Code ?? (ex as AppException)?.Code}: {ex.Message}");
                return 1;
            }

            Console.WriteLine($"Indexed {index.Chunks} chunks of {index.Documents} documents: {index.Embedded} embedded in {index.EmbeddingRequests} request(s), {index.FromCache} from the cache file, {index.FromStore} kept.");
            Console.WriteLine();

            var ask = scope.ServiceProvider.GetRequiredService<RagAskService>();
            var searchService = scope.ServiceProvider.GetRequiredService<KnowledgeSearchService>();
            foreach (var evalCase in cases)
            {
                var attempt = await RunCaseAsync(evalCase, ask, searchService, retrievalOnly);
                attempts.Add(attempt);
                Console.WriteLine(
                    $"[{attempts.Count,2}/{cases.Count}] {evalCase.Id,-32} {(attempt.Passed ? "PASS" : "FAIL")} {attempt.Outcome,-26} rank={(attempt.Rank?.ToString(CultureInfo.InvariantCulture) ?? "-"),-2} cos={(attempt.TopCosine?.ToString("F3", CultureInfo.InvariantCulture) ?? "-"),-5} {attempt.LatencyMs / 1000.0,5:F1}s {attempt.Tokens,6:N0} tok"
                    + (attempt.Passed || attempt.Detail is null ? "" : $"  <- {attempt.Detail}"));
            }

            usageRows = (await scope.ServiceProvider.GetRequiredService<LlmUsageQueries>().SummariseAsync(null, null, CancellationToken.None)).Rows;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // temp directory outside the repo
            }
        }

        var finished = DateTime.UtcNow;
        var answerable = attempts.Where(a => a.Case.Answerable).ToList();
        var unanswerable = attempts.Where(a => !a.Case.Answerable).ToList();
        var k = 6;
        var hit = answerable.Count(a => a.Rank is not null);
        var mrr = answerable.Count == 0 ? 0 : answerable.Average(a => a.Rank is { } r ? 1.0 / r : 0);
        var aboveThreshold = answerable.Count(a => a.ExpectedAboveThreshold == true);
        var citedRight = answerable.Count(a => a.Passed);
        var refusedRight = unanswerable.Count(a => a.Passed);
        var passed = attempts.Count(a => a.Passed);
        var rate = attempts.Count == 0 ? 0 : (double)passed / attempts.Count;
        var totals = LlmUsageReport.Sum(usageRows);
        var errors = attempts.Count(a => a.ErrorCode is not null);

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"RAG eval  model={model}  embeddings={embeddingModel}  prompt={promptVersion}{(retrievalOnly ? "  (retrieval only)" : "")}");
        sb.AppendLine(new string('-', 100));
        sb.AppendLine($"Retrieval only (answerable, n={answerable.Count}):  hit@{k} = {hit}/{answerable.Count} ({Pct(hit, answerable.Count)})   MRR = {mrr:F3}   expected chunk above threshold = {aboveThreshold}/{answerable.Count}");
        if (!retrievalOnly)
        {
            sb.AppendLine($"Answers (answerable):   cited the right section = {citedRight}/{answerable.Count} ({Pct(citedRight, answerable.Count)})");
            sb.AppendLine($"Refusals (unanswerable): refused correctly = {refusedRight}/{unanswerable.Count} ({Pct(refusedRight, unanswerable.Count)})   [by threshold: {unanswerable.Count(a => a.RefusalReason == RefusalReasons.BelowThreshold)}, by model: {unanswerable.Count(a => a.RefusalReason is RefusalReasons.ModelDeclined or RefusalReasons.NoValidCitation)}]");
        }
        else
        {
            sb.AppendLine($"Refusals (unanswerable): refused by the relevance threshold alone = {refusedRight}/{unanswerable.Count} ({Pct(refusedRight, unanswerable.Count)}); the model is the second line of defence and was not called");
        }

        sb.AppendLine($"OVERALL: {passed}/{attempts.Count} = {100 * rate:F1}%  (target >= {100 * Target:F0}%){(errors > 0 ? $"  [{errors} case(s) ended in a provider error]" : "")}");
        var (overallPass, unanswerablePass) = Gates(passed, attempts.Count, refusedRight, unanswerable.Count);
        sb.AppendLine($"GATE overall >= {100 * Target:F0}%: {(overallPass ? "PASS" : "FAIL")}");
        sb.AppendLine($"GATE unanswerable refused = {refusedRight}/{unanswerable.Count} (target {100 * UnanswerableTarget:F0}%): {(unanswerablePass ? "PASS" : "FAIL")}{(unanswerablePass ? "" : "  <- " + string.Join(", ", unanswerable.Where(a => !a.Passed).Select(a => a.Case.Id)))}");
        Console.Write(sb);
        Console.WriteLine();
        Console.Write(LlmUsageReport.Format(usageRows));

        var resultsDirectory = Path.Combine(repoRoot, "evals", "results");
        Directory.CreateDirectory(resultsDirectory);
        var resultsPath = Path.Combine(resultsDirectory, $"rag-{started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}{(retrievalOnly ? "-retrieval" : "")}.json");
        var report = new
        {
            suite = "rag",
            isModelResult = true,
            retrievalOnly,
            invalid = false,
            costIsEstimate = true,
            pricingNote = EvalHonesty.PricingNote,
            startedAt = started,
            finishedAt = finished,
            model,
            embeddingModel,
            promptVersion,
            runsPerCase = 1,
            target = Target,
            gates = new
            {
                overall = new { target = Target, pass = overallPass },
                unanswerable = new { target = UnanswerableTarget, refused = refusedRight, of = unanswerable.Count, pass = unanswerablePass, failedCases = unanswerable.Where(a => !a.Passed).Select(a => a.Case.Id).ToArray() },
            },
            index = new { index.Documents, index.Chunks, index.Embedded, index.FromCache, index.FromStore, index.EmbeddingRequests, index.PromptTokens },
            summary = new
            {
                cases = attempts.Count,
                answerable = answerable.Count,
                unanswerable = unanswerable.Count,
                passed,
                passRate = Math.Round(rate, 4),
                retrieval = new { k, hitAtK = Math.Round(answerable.Count == 0 ? 0 : (double)hit / answerable.Count, 4), hits = hit, mrr = Math.Round(mrr, 4), expectedAboveThreshold = aboveThreshold },
                answer = retrievalOnly ? null : new { citedRightSection = citedRight, of = answerable.Count, rate = Math.Round(answerable.Count == 0 ? 0 : (double)citedRight / answerable.Count, 4) },
                refusal = new
                {
                    correct = refusedRight,
                    of = unanswerable.Count,
                    rate = Math.Round(unanswerable.Count == 0 ? 0 : (double)refusedRight / unanswerable.Count, 4),
                    byThreshold = unanswerable.Count(a => a.RefusalReason == RefusalReasons.BelowThreshold),
                    byModel = unanswerable.Count(a => a.RefusalReason is RefusalReasons.ModelDeclined or RefusalReasons.NoValidCitation),
                },
                providerErrors = errors,
                promptTokens = totals.PromptTokens,
                candidateTokens = totals.CandidateTokens,
                thoughtTokens = totals.ThoughtTokens,
                totalTokens = totals.TotalTokens,
                estimatedCostUsd = Math.Round(totals.CostUsd, 6),
                llmCalls = usageRows.Where(r => r.Kind == "llm").Sum(r => r.Calls),
                embeddingCalls = usageRows.Where(r => r.Kind == "embedding").Sum(r => r.Calls),
            },
            cases = attempts.Select(a => new
            {
                id = a.Case.Id,
                category = a.Case.Category,
                question = a.Case.Question,
                expected = a.Case.Expect is null ? null : $"{a.Case.Expect.Source} / {a.Case.Expect.Section}",
                passed = a.Passed,
                outcome = a.Outcome,
                detail = a.Detail,
                answered = a.Answered,
                refusalReason = a.RefusalReason,
                rankOfExpected = a.Rank,
                expectedAboveThreshold = a.ExpectedAboveThreshold,
                topCosine = a.TopCosine,
                retrieved = a.Retrieved,
                cited = a.Cited,
                answer = a.Answer,
                errorCode = a.ErrorCode,
                tokens = a.Tokens,
                thoughtTokens = a.ThoughtTokens,
                latencyMs = a.LatencyMs,
            }),
            usage = usageRows.Select(r => new { r.Feature, r.Model, r.Kind, r.Calls, r.Failures, r.PromptTokens, r.CandidateTokens, r.ThoughtTokens, r.CostUsd }),
        };
        await File.WriteAllTextAsync(
            resultsPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n");
        Console.WriteLine($"Results: {Path.GetRelativePath(repoRoot, resultsPath)}");

        if (persist)
        {
            var devDatabase = new SqliteConnectionStringBuilder(configuration["Database:ConnectionString"] ?? throw new InvalidOperationException("DB_CONNECTION is not configured.")).DataSource;
            await using var provider = AgentEvalRunner.BuildProvider(devDatabase, withLlm: false);
            await DatabaseInitializer.MigrateAsync(provider);
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
            var row = new EvalRun
            {
                Suite = "rag",
                StartedAt = started,
                FinishedAt = finished,
                Model = $"{model} + {embeddingModel}",
                PromptVersion = promptVersion,
                RunsPerCase = 1,
                Cases = attempts.Count,
                Attempts = attempts.Count,
                Passed = passed,
                PassRate = rate,
                PromptTokens = (int)totals.PromptTokens,
                CandidateTokens = (int)totals.CandidateTokens,
                ThoughtTokens = (int)totals.ThoughtTokens,
                CostUsd = totals.CostUsd,
                ResultsFile = Path.GetRelativePath(repoRoot, resultsPath).Replace('\\', '/'),
                Results = attempts.Select(a => new EvalResult
                {
                    CaseId = a.Case.Id,
                    Category = a.Case.Category,
                    Attempt = 1,
                    Passed = a.Passed,
                    Outcome = a.Outcome,
                    Detail = a.Detail is { Length: > 2000 } d ? d[..2000] : a.Detail,
                    AgentStatus = a.Answered ? "answered" : "refused",
                    ErrorCode = a.ErrorCode,
                    ToolCalls = 0,
                    Tokens = a.Tokens,
                    LatencyMs = a.LatencyMs,
                }).ToList(),
            };
            db.EvalRuns.Add(row);
            await db.SaveChangesAsync();
            Console.WriteLine($"Saved eval_runs #{row.Id} with {row.Results.Count} eval_results to the configured database.");
        }

        return overallPass && unanswerablePass ? 0 : 1;
    }

    private static async Task<RagAttempt> RunCaseAsync(RagCase evalCase, RagAskService ask, KnowledgeSearchService search, bool retrievalOnly)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (retrievalOnly)
            {
                var retrieval = await Retry(() => search.SearchAsync(1, evalCase.Question, null, CancellationToken.None));
                var rank = RankOf(evalCase, retrieval.Chunks.Select(c => (c.Source, c.Section)).ToList());
                var expectedAbove = evalCase.Answerable ? retrieval.Chunks.Any(c => Matches(evalCase, c.Source, c.Section) && c.AboveThreshold) : (bool?)null;
                var ok = evalCase.Answerable ? rank is not null && expectedAbove == true : retrieval.BelowThreshold;
                return new RagAttempt(
                    evalCase, ok, evalCase.Answerable ? (ok ? "retrieved_expected" : rank is null ? "expected_not_retrieved" : "expected_below_threshold") : (ok ? "refused_threshold" : "not_refused_by_threshold"),
                    ok ? null : evalCase.Answerable ? $"expected section rank={rank?.ToString(CultureInfo.InvariantCulture) ?? "none"}, above threshold={expectedAbove}" : $"top cosine {retrieval.TopCosine:F3} >= threshold {retrieval.MinCosine:F2}",
                    !retrieval.BelowThreshold, retrieval.BelowThreshold ? RefusalReasons.BelowThreshold : null, rank, expectedAbove, retrieval.TopCosine,
                    retrieval.Chunks.Select(c => $"{c.Source} / {c.Section} p.{c.Page} cos={c.Cosine:F3}").ToList(), [], "", null, 0, 0, (int)watch.ElapsedMilliseconds);
            }

            var response = await Retry(() => ask.AskAsync(1, evalCase.Question, CancellationToken.None));
            var rankOfExpected = RankOf(evalCase, response.Retrieved.Select(r => (r.Source, r.Section)).ToList());
            var expectedAboveThreshold = evalCase.Answerable ? response.Retrieved.Any(r => Matches(evalCase, r.Source, r.Section) && r.AboveThreshold) : (bool?)null;
            var cited = response.Citations.Select(c => $"{c.Source} / {c.Section} p.{c.Page}").ToList();
            var tokens = response.Usage?.TotalTokens ?? 0;
            var thought = response.Usage?.ThoughtTokens ?? 0;
            var topCosine = response.Retrieved.Max(r => r.Cosine);
            var retrieved = response.Retrieved.Select(r => $"{r.Source} / {r.Section} p.{r.Page} cos={r.Cosine:F3}").ToList();

            if (evalCase.Answerable)
            {
                var citedExpected = response.Answered && response.Citations.Any(c => Matches(evalCase, c.Source, c.Section));
                var outcome = citedExpected ? "cited_expected"
                    : !response.Answered ? (response.RefusalReason == RefusalReasons.BelowThreshold ? "wrongly_refused_threshold" : "wrongly_refused_model")
                    : "answered_wrong_section";
                return new RagAttempt(
                    evalCase, citedExpected, outcome, citedExpected ? null : $"expected {evalCase.Expect!.Source} / {evalCase.Expect.Section}; cited [{string.Join("; ", cited)}]; {Clip(response.Answer, 160)}",
                    response.Answered, response.RefusalReason, rankOfExpected, expectedAboveThreshold, topCosine, retrieved, cited, response.Answer, null, tokens, thought, (int)watch.ElapsedMilliseconds);
            }

            var refused = !response.Answered;
            return new RagAttempt(
                evalCase, refused, refused ? (response.RefusalReason == RefusalReasons.BelowThreshold ? "refused_threshold" : "refused_model") : "answered_but_should_refuse",
                refused ? null : $"answered: {Clip(response.Answer, 200)}", response.Answered, response.RefusalReason, null, null, topCosine, retrieved, cited, response.Answer, null, tokens, thought, (int)watch.ElapsedMilliseconds);
        }
        catch (LlmException ex)
        {
            return new RagAttempt(evalCase, false, "provider_error", $"{ex.Code}: {ex.Message}", false, null, null, null, null, [], [], "", ex.Code, 0, 0, (int)watch.ElapsedMilliseconds);
        }
    }

    /// <summary>A rate-limited call is repeated after the wait the provider asked for (at most twice); the error text is kept if it still fails.</summary>
    private static async Task<T> Retry<T>(Func<Task<T>> call)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (LlmException ex) when (ex.Code == LlmErrorCodes.RateLimited && attempt < 2)
            {
                var wait = ex.RetryAfter is { } w && w < TimeSpan.FromSeconds(90) ? w : TimeSpan.FromSeconds(30);
                Console.WriteLine($"          rate limited ({Clip(ex.Message, 140)}); waiting {wait.TotalSeconds:0} s");
                await Task.Delay(wait + TimeSpan.FromSeconds(1));
            }
        }
    }

    private static bool Matches(RagCase evalCase, string source, string section) =>
        evalCase.Expect is { } e
        && string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase)
        && string.Equals(e.Section.Trim(), section.Trim(), StringComparison.OrdinalIgnoreCase);

    private static int? RankOf(RagCase evalCase, IReadOnlyList<(string Source, string Section)> ranking)
    {
        if (!evalCase.Answerable)
        {
            return null;
        }

        for (var i = 0; i < ranking.Count; i++)
        {
            if (Matches(evalCase, ranking[i].Source, ranking[i].Section))
            {
                return i + 1;
            }
        }

        return null;
    }

    private static string Pct(int part, int whole) => whole == 0 ? "n/a" : $"{100.0 * part / whole:F1}%";

    private static string Clip(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ");
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
