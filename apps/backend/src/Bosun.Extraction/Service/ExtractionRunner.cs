using System.Collections.Concurrent;
using System.Text.Json;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ExtractionEntity = Bosun.Domain.Entities.Extraction;

namespace Bosun.Extraction;

/// <summary>
/// Runs one extraction: loads the stored PDF, runs <see cref="ExtractionPipeline"/>, and writes the result (status, page report,
/// token spend, rows) to the database. Never throws: every outcome, failures included, ends up on the extraction row.
/// </summary>
public sealed class ExtractionRunner(
    IBosunDbContext db,
    WriteGate gate,
    IDocumentStore store,
    ExtractionPipeline pipeline,
    ExtractionPrompts prompts,
    TimeProvider clock,
    ILogger<ExtractionRunner> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(int extractionId, CancellationToken ct)
    {
        var extraction = await db.Extractions.Include(e => e.SourceDocument).FirstAsync(e => e.Id == extractionId, CancellationToken.None);
        try
        {
            extraction.Status = ExtractionStatus.Running;
            extraction.StartedAt = clock.GetUtcNow().UtcDateTime;
            extraction.Phase = "triage";
            extraction.PromptVersion = prompts.Version;
            await SaveAsync();

            var pdf = await store.ReadAsync(extraction.SourceDocument!.StoredName, ct);
            var projectTags = (await db.Tags.AsNoTracking().Where(t => t.ProjectId == extraction.ProjectId).Select(t => t.TagName).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            var result = await pipeline.RunAsync(pdf, projectTags, async p =>
            {
                extraction.Phase = p.Phase;
                extraction.PagesDone = p.PagesDone;
                if (p.PagesTotal > 0)
                {
                    extraction.PagesTotal = p.PagesTotal;
                }

                await SaveAsync();
            }, ct);

            extraction.PagesJson = JsonSerializer.Serialize(result.Pages, Json);
            extraction.Model = result.Model;
            extraction.LlmCalls = result.Calls;
            extraction.PromptTokens = result.Usage.PromptTokens;
            extraction.CandidateTokens = result.Usage.CandidateTokens;
            extraction.ThoughtTokens = result.Usage.ThoughtTokens;
            extraction.TotalTokens = result.Usage.TotalTokens;
            extraction.CostUsd = result.CostUsd;
            extraction.ErrorCode = result.ErrorCode;
            extraction.ErrorMessage = result.ErrorMessage is { Length: > 1000 } m ? m[..1000] : result.ErrorMessage;

            foreach (var record in result.Rows)
            {
                var row = new ExtractedRow
                {
                    ExtractionId = extraction.Id,
                    Seq = record.State.Seq,
                    Page = record.Page,
                    SourcePath = record.Path,
                    SourceRef = record.SourceRef is { Length: > 200 } r ? r[..200] : record.SourceRef,
                    SourceSnippet = record.Snippet is { Length: > 1000 } sn ? sn[..1000] : record.Snippet,
                    BboxJson = record.Bbox is null ? null : JsonSerializer.Serialize(record.Bbox, Json),
                    State = ExtractedRowState.Pending,
                };
                ExtractionService.Store(row, record.State);
                db.ExtractedRows.Add(row);
            }

            extraction.Status = result.Status;
            Finish(extraction, result.Pages.Count(p => p.Status != "skipped"));
            await SaveAsync();
        }
        catch (OperationCanceledException)
        {
            await FailAsync(extraction, "cancelled", "The server stopped while the document was being read.");
        }
        catch (LlmException ex)
        {
            logger.LogWarning("Extraction {Id} failed: {Code} {Message}", extractionId, ex.Code, ex.Message);
            await FailAsync(extraction, ex.Code, ex.Message);
        }
        catch (AppException ex)
        {
            await FailAsync(extraction, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Extraction {Id} crashed", extractionId);
            await FailAsync(extraction, "internal_error", "The extraction failed unexpectedly. See the server log.");
        }
    }

    private void Finish(ExtractionEntity extraction, int readablePages)
    {
        extraction.Phase = null;
        extraction.FinishedAt = clock.GetUtcNow().UtcDateTime;
        extraction.PagesDone = readablePages;
        extraction.PagesTotal = readablePages;
    }

    private async Task FailAsync(ExtractionEntity extraction, string code, string message)
    {
        try
        {
            db.ChangeTracker.Clear();
            var row = await db.Extractions.FirstAsync(e => e.Id == extraction.Id, CancellationToken.None);
            row.Status = ExtractionStatus.Failed;
            row.Phase = null;
            row.FinishedAt = clock.GetUtcNow().UtcDateTime;
            row.ErrorCode = code;
            row.ErrorMessage = message.Length > 1000 ? message[..1000] : message;
            await SaveAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the failure of extraction {Id}", extraction.Id);
        }
    }

    private async Task SaveAsync()
    {
        using (await gate.EnterAsync(CancellationToken.None))
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}

/// <summary>
/// Starts extraction runs in the background, one at a time (the provider quota is the scarce resource), and fails the runs a restart
/// cut off. Progress is read by polling <c>GET /api/extractions/{id}</c> (status, phase, pagesDone/pagesTotal).
/// </summary>
public sealed class ExtractionRunManager(IServiceScopeFactory scopes, ILogger<ExtractionRunManager> logger, TimeProvider clock)
{
    private readonly ConcurrentDictionary<int, Task> active = new();
    private readonly SemaphoreSlim one = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();

    public bool IsActive(int extractionId) => active.ContainsKey(extractionId);

    /// <summary>The background task of a run that is still queued or executing, for callers that must wait for it (tests, shutdown).</summary>
    public Task? Completion(int extractionId) => active.TryGetValue(extractionId, out var task) ? task : null;

    public void Start(int extractionId)
    {
        var task = Task.Run(() => RunQueuedAsync(extractionId), CancellationToken.None);
        active[extractionId] = task;
        _ = task.ContinueWith(_ => active.TryRemove(extractionId, out Task? _), TaskScheduler.Default);
    }

    public void CancelAll() => shutdown.Cancel();

    private async Task RunQueuedAsync(int extractionId)
    {
        try
        {
            await one.WaitAsync(shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ExtractionRunner>().RunAsync(extractionId, shutdown.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Extraction {Id} escaped its runner", extractionId);
        }
        finally
        {
            one.Release();
        }
    }

    /// <summary>At start-up nothing can still be running: whatever says so was cut off by a restart.</summary>
    public async Task RecoverOrphansAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
        var orphans = await db.Extractions.Where(e => e.Status == ExtractionStatus.Queued || e.Status == ExtractionStatus.Running).ToListAsync(ct);
        foreach (var extraction in orphans)
        {
            extraction.Status = ExtractionStatus.Failed;
            extraction.Phase = null;
            extraction.FinishedAt = clock.GetUtcNow().UtcDateTime;
            extraction.ErrorCode = "server_restarted";
            extraction.ErrorMessage = "The server restarted while this extraction was in progress. Upload the document again.";
        }

        if (orphans.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Marked {Count} orphaned extraction(s) as failed", orphans.Count);
        }
    }
}

public static class ExtractionRegistration
{
    /// <summary>The pipeline, the document store, the renderer, the service and the background manager.</summary>
    public static IServiceCollection AddBosunExtraction(this IServiceCollection services)
    {
        services.AddOptions<ExtractionOptions>().BindConfiguration("Extraction");
        services.AddSingleton<ExtractionPrompts>();
        services.AddSingleton<IPageRenderer, PdfiumPageRenderer>();
        services.AddSingleton<IDocumentStore, FileDocumentStore>();
        services.AddSingleton<ExtractionRunManager>();
        services.AddScoped<RowReader>();
        services.AddScoped<ExtractionPipeline>();
        services.AddScoped<ExtractionRunner>();
        services.AddScoped<ExtractionService>();
        return services;
    }
}
