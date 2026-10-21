using Bosun.Agent;
using Bosun.Application;
using Bosun.Application.Rag;

namespace Bosun.Api;

public sealed record AskBody(string? Question);

/// <summary>
/// RAG over the project documents (plan s6.4). The status read follows the configurator rule (open in Development, a session
/// elsewhere). Reindex, search and ask spend embedding or LLM quota (and search/ask are reachable by any page on the machine through
/// loopback), so they need a session in EVERY environment (401 unauthorized).
/// </summary>
public static class KnowledgeEndpoints
{
    public static void MapKnowledge(this WebApplication app, bool readsRequireLogin)
    {
        var reads = app.MapGroup("/api").WithTags("Knowledge");
        if (readsRequireLogin)
        {
            reads.RequireAuthorization();
        }

        reads.MapGet("/projects/{projectId:int}/knowledge", async (int projectId, KnowledgeIngestService ingest, CancellationToken ct) =>
                TypedResults.Ok(await ingest.StatusAsync(projectId, ct)))
            .WithSummary("Index status: { indexed, documents, chunks, embeddedChunks, documentsOnDisk, embeddingModel, dimensions, lastIndexedAt, sources[{source, chunks, pages}] }. indexed=false means 'not indexed' (nothing to search yet).");

        var writes = app.MapGroup("/api").WithTags("Knowledge").RequireAuthorization();

        writes.MapPost("/projects/{projectId:int}/knowledge/reindex", async (int projectId, KnowledgeIngestService ingest, CancellationToken ct) =>
                TypedResults.Ok(await ingest.ReindexAsync(projectId, cacheOnly: false, ct)))
            .WithSummary("Chunk the Markdown documents (seed/docs) and embed what is new or changed; unchanged chunks (same content hash) are not embedded again. 200 { documents, chunks, embedded, fromStore, fromCache, embeddingRequests, promptTokens, ... }; 409 reindex_in_progress; 422 no_documents; llm_* codes when the embedding provider fails (nothing is changed then).");

        writes.MapGet("/projects/{projectId:int}/knowledge/search", async (int projectId, string? q, int? k, KnowledgeSearchService search, CancellationToken ct) =>
                TypedResults.Ok(await search.SearchAsync(projectId, q ?? "", k, ct)))
            .WithSummary("Hybrid retrieval for debugging: the fused top-k chunks with rrf score, cosine, bm25, ranks, aboveThreshold and flagged (instruction-like text found in the chunk; it stays data for the model). 400 invalid_question; 409 knowledge_not_indexed; llm_* codes when the question cannot be embedded.");

        writes.MapPost("/projects/{projectId:int}/ask", async (int projectId, AskBody? body, RagAskService ask, CancellationToken ct) =>
                TypedResults.Ok(await ask.AskAsync(projectId, body?.Question ?? "", ct)))
            .RequireRateLimiting(Auth.AuthSetup.AskRateLimitPolicy)
            .WithSummary("Rate limited per user (20 per minute; 429 rate_limited with Retry-After). Grounded answer from the project documents: { question, answered, answer, citations[{source, section, page}], refusalReason, droppedCitations, retrieved[{source, section, page, score, cosine, aboveThreshold, usedForAnswer, flagged, isExclusion}], model, promptVersion, usage, latencyMs }. answered=false is a refusal (not in the project documents). Citations are verified against the retrieved passages. 400 invalid_question; 409 knowledge_not_indexed; llm_* codes on provider failures.");
    }
}
