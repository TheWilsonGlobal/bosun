using System.Globalization;
using System.Text.RegularExpressions;
using Bosun.Application.Llm;
using Bosun.Evals.Fixtures;
using Bosun.Evals.Offline;
using Bosun.Extraction;
using Bosun.Infrastructure.Persistence;
using Bosun.Infrastructure.Seed;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Evals.Extraction;

/// <summary>The outcome of one fixture run: scores split by what read the rows, bands, calibration, flags and spend.</summary>
public sealed class FixtureRun
{
    public required string Fixture { get; init; }

    public required string Title { get; init; }

    public required PipelineResult Result { get; init; }

    /// <summary>Rows read from the text layer (truth kind "text" against rows from the text path).</summary>
    public required ExtractionScore Text { get; init; }

    /// <summary>Rows read from page images (truth kind "scanned" against rows from the vision path).</summary>
    public required ExtractionScore Scanned { get; init; }

    public required IReadOnlyList<LlmUsageRow> Usage { get; init; }

    public required double Seconds { get; init; }

    public required FixtureTruth Truth { get; init; }

    public int Retries { get; init; }

    public IEnumerable<RowOutcome> AllRows => Text.Rows.Concat(Scanned.Rows);

    /// <summary>The provider's own error text when the run ended on a spent quota (null otherwise).</summary>
    public string? QuotaError { get; init; }
}

/// <summary>
/// Runs the extraction pipeline over a fixture against the REAL model (or, with <c>offline</c>, a scripted faithful reader that proves
/// the plumbing and nothing else) on a copy of the seeded database, and scores it against the ground truth.
/// </summary>
public sealed class ExtractionEvalRunner(string seedFile, string workDirectory, LlmThrottle? throttle, bool offline)
{
    public const int MaxInfrastructureRetries = 2;

    private static readonly HashSet<string> ProviderSideCodes = [LlmErrorCodes.RateLimited, LlmErrorCodes.Unavailable, LlmErrorCodes.Timeout];

    public string Model { get; private set; } = "";

    public string PromptVersion { get; private set; } = "";

    public async Task<FixtureRun> RunAsync(FixtureTruth truth, byte[] pdf, CancellationToken ct)
    {
        var retries = 0;
        while (true)
        {
            var run = await RunOnceAsync(truth, pdf, ct);
            var code = run.Result.ErrorCode;
            if (run.QuotaError is not null || code is null || !ProviderSideCodes.Contains(code) || retries >= MaxInfrastructureRetries || offline)
            {
                return run;
            }

            retries++;
            var wait = TimeSpan.FromSeconds(30 * retries);
            Console.WriteLine($"          provider said {code} for {truth.Id}; waiting {wait.TotalSeconds:0} s and repeating the document ({retries}/{MaxInfrastructureRetries})");
            await Task.Delay(wait, ct);
        }
    }

    private async Task<FixtureRun> RunOnceAsync(FixtureTruth truth, byte[] pdf, CancellationToken ct)
    {
        var dir = Path.Combine(workDirectory, $"{truth.Id}-{Guid.NewGuid().ToString("N")[..6]}");
        var dbPath = SeedDatabase.CopyTo(seedFile, dir);
        try
        {
            await using var provider = BuildProvider(dbPath, truth);
            await using var scope = provider.CreateAsyncScope();
            var llm = scope.ServiceProvider.GetRequiredService<ILlmClient>();
            Model = llm.DefaultModel;
            PromptVersion = scope.ServiceProvider.GetRequiredService<ExtractionPrompts>().Version;

            var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
            var projectTags = (await db.Tags.AsNoTracking().Select(t => t.TagName).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await scope.ServiceProvider.GetRequiredService<ExtractionPipeline>().RunAsync(pdf, projectTags, null, ct);
            watch.Stop();

            var usage = (await scope.ServiceProvider.GetRequiredService<LlmUsageQueries>().SummariseAsync(null, null, ct)).Rows;
            return new FixtureRun
            {
                Fixture = truth.Id,
                Title = truth.Title,
                Result = result,
                Text = ExtractionScorer.Score([.. truth.Rows.Where(r => r.Kind == "text")], [.. result.Rows.Where(r => r.Path == "text")]),
                Scanned = ExtractionScorer.Score([.. truth.Rows.Where(r => r.Kind == "scanned")], [.. result.Rows.Where(r => r.Path == "vision")]),
                Usage = usage,
                Seconds = watch.Elapsed.TotalSeconds,
                Truth = truth,
                QuotaError = QuotaText(result),
            };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // temp files; a locked leftover is harmless
            }
        }
    }

    /// <summary>A daily quota (the provider asks for a wait of many minutes) is not something to retry: the run stops and says so.</summary>
    public static string? QuotaText(PipelineResult result)
    {
        if (result.ErrorCode != LlmErrorCodes.RateLimited)
        {
            return null;
        }

        var text = result.Pages.FirstOrDefault(p => p.ErrorCode == LlmErrorCodes.RateLimited && p.ErrorMessage?.StartsWith("Not attempted", StringComparison.Ordinal) != true)?.ErrorMessage ?? result.ErrorMessage;
        var wait = Regex.Match(text ?? "", @"Retry in about (?<s>\d+) s", RegexOptions.CultureInvariant);
        return wait.Success && int.Parse(wait.Groups["s"].Value, CultureInfo.InvariantCulture) > 300 ? text : null;
    }

    private ServiceProvider BuildProvider(string dbPath, FixtureTruth truth)
    {
        if (offline)
        {
            var fake = new TableParsingLlm(page => TruthReader.ReadRowsOf(truth.Rows.Where(r => r.Page == page)));
            return AgentEvalRunner.BuildProvider(dbPath, withLlm: false, configure: services =>
            {
                services.AddSingleton<ILlmClient>(fake);
                services.AddSingleton<StructuredLlm>();
                services.AddBosunExtraction();
                services.AddScoped<LlmUsageQueries>();
            });
        }

        return AgentEvalRunner.BuildProvider(dbPath, withLlm: true, throttle: throttle, configure: services => services.AddBosunExtraction());
    }
}
