using System.Security.Claims;
using Bosun.Api.Auth;
using Bosun.Application;
using Bosun.Extraction;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bosun.Api;

/// <summary>
/// Document extraction (plan s6.3): upload a customer PDF, poll the extraction, review and correct rows, accept them into a DRAFT
/// change set. Uploading, editing and accepting need a signed-in human; reads follow the configurator rule (open in Development, a
/// session elsewhere). Nothing here approves or applies a change set: accepting only creates a draft that goes through the normal
/// validate / approve / apply path.
/// </summary>
public static class ExtractionEndpoints
{
    /// <summary>
    /// The upload cap itself (20 MB) is enforced by the service while it copies the file, so a file a little over the cap gets a clean
    /// 413 problem body (document_too_large). This coarser limit on the whole request (file + multipart framing) only stops absurd bodies
    /// early; Kestrel answers those with a bare 413 or resets the connection.
    /// </summary>
    private const long RequestLimitBytes = 26L * 1024 * 1024;

    public static void MapExtraction(this WebApplication app, bool readsRequireLogin)
    {
        var reads = app.MapGroup("/api").WithTags("Extraction");
        if (readsRequireLogin)
        {
            reads.RequireAuthorization();
        }

        reads.MapGet("/projects/{projectId:int}/documents", async (int projectId, ExtractionService svc, CancellationToken ct) =>
                TypedResults.Ok(await svc.ListDocumentsAsync(projectId, ct)))
            .WithSummary("Uploaded documents of a project, newest first, with the state of their latest extraction.");

        reads.MapGet("/extractions/{id:int}", async (int id, ExtractionService svc, CancellationToken ct) =>
                TypedResults.Ok(await svc.GetAsync(id, ct)))
            .WithSummary("Status (poll this while queued/running), per-page triage, rows with per-field value/confidence/band/violations/flags, summary counts.");

        reads.MapGet("/extractions/{id:int}/pages/{page:int}/image",
                async (int id, int page, int? dpi, int? rowId, ExtractionService svc, HttpContext http, CancellationToken ct) =>
                {
                    var image = await svc.GetPageImageAsync(id, page, dpi, rowId, ct);
                    http.Response.Headers.CacheControl = "private, max-age=3600";
                    return TypedResults.File(image.Png, "image/png");
                })
            .WithSummary("PNG of a page (dpi 50-300, default 110); with rowId the region of that row and its neighbours.");

        var writes = app.MapGroup("/api").WithTags("Extraction").RequireAuthorization();

        writes.MapPost("/projects/{projectId:int}/documents",
                async (int projectId, IFormFile? file, ExtractionService svc, ClaimsPrincipal principal, CancellationToken ct) =>
                {
                    if (file is null)
                    {
                        throw new AppException(ErrorKind.Validation, ExtractionErrorCodes.NoFile, "Send the PDF as multipart/form-data in a part named 'file'.");
                    }

                    await using var stream = file.OpenReadStream();
                    var result = await svc.UploadAsync(projectId, stream, file.FileName, principal.ToUser(), ct);
                    return TypedResults.Accepted($"/api/extractions/{result.ExtractionId}", result);
                })
            .DisableAntiforgery() // the session cookie is SameSite=Strict (see AuthSetup), which is the CSRF defence for this API
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(RequestLimitBytes))
            .WithSummary("Upload a PDF (multipart part 'file', max 20 MB, %PDF magic bytes checked). 202 { documentId, extractionId }; the extraction runs in the background.");

        writes.MapPost("/documents/{documentId:int}/extractions",
                async (int documentId, ExtractionService svc, ClaimsPrincipal principal, CancellationToken ct) =>
                {
                    var result = await svc.ExtractAgainAsync(documentId, principal.ToUser(), ct);
                    return TypedResults.Accepted($"/api/extractions/{result.ExtractionId}", result);
                })
            .WithSummary("Read an uploaded document again (retry after a provider failure). 202 { documentId, extractionId }; 409 extraction_already_running.");

        writes.MapPatch("/extractions/{id:int}/rows/{rowId:int}",
                async (int id, int rowId, PatchRowBody? body, ExtractionService svc, CancellationToken ct) =>
                    TypedResults.Ok(await svc.PatchRowAsync(id, rowId, body ?? new PatchRowBody(null, null), ct)))
            .WithSummary("Human edit of a row: { fields: { tagName: ..., rangeHigh: ... }, excluded: bool }. Re-validates and re-scores the row (and rows that share its tag).");

        writes.MapPost("/extractions/{id:int}/accept",
                async Task<Created<AcceptResultDto>> (int id, AcceptBody? body, ExtractionService svc, ClaimsPrincipal principal, CancellationToken ct) =>
                {
                    var result = await svc.AcceptAsync(id, body?.RowIds, principal.ToUser(), ct, body?.AllowFlagged);
                    return TypedResults.Created($"/api/change-sets/{result.ChangeSets[0].Id}", result);
                })
            .WithSummary("Turn the chosen rows (default: all open rows) into DRAFT change sets (source=extraction). 422 unresolved_must_fix while must-fix rows are chosen; 422 flagged_rows_need_override while chosen rows are suspected_injection and not listed in body.allowFlagged; 409 rows_already_decided on a double accept. Never approves or applies.");

        writes.MapDelete("/documents/{documentId:int}",
                async Task<NoContent> (int documentId, ExtractionService svc, CancellationToken ct) =>
                {
                    await svc.RemoveDocumentAsync(documentId, ct);
                    return TypedResults.NoContent();
                })
            .WithSummary("Delete an uploaded document with its extractions, rows and stored file (change sets already made from it stay). 204; 404; 409 extraction_already_running while it is being read.");
    }
}

/// <summary>Fails extractions that a restart cut off, and stops the background runner on shutdown.</summary>
public sealed class ExtractionLifecycle(ExtractionRunManager manager, ILogger<ExtractionLifecycle> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await manager.RecoverOrphansAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Typically the database is not migrated yet (non-Development): not a reason to stop the server.
            logger.LogWarning(ex, "Could not check for orphaned extractions");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        manager.CancelAll();
        return Task.CompletedTask;
    }
}
