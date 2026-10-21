using Bosun.Application.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bosun.Application.Rag;

/// <summary>One chunk of a retrieval, with everything needed to debug why it ranked where it did.</summary>
/// <param name="Score">The fused RRF score (rank based; only useful for ordering).</param>
/// <param name="Cosine">Cosine similarity to the question, null when the vector retriever did not return the chunk.</param>
/// <param name="Bm25">Keyword score (higher is better), null when the keyword retriever did not return the chunk.</param>
/// <param name="AboveThreshold">Cosine at or above the relevance threshold: only these chunks may be used to answer.</param>
public sealed record RetrievedChunk(
    int Id,
    string Source,
    string Section,
    int Page,
    string Text,
    double Score,
    double? Cosine,
    double? Bm25,
    int? VectorRank,
    int? KeywordRank,
    bool AboveThreshold);

/// <param name="Chunks">The fused top-k ranking, threshold flags included (what the debug endpoint shows, and what hit@k is measured on).</param>
/// <param name="Relevant">The chunks of <paramref name="Chunks"/> that passed the threshold: all that an answer may be based on.</param>
public sealed record RetrievalResult(string Query, IReadOnlyList<RetrievedChunk> Chunks, IReadOnlyList<RetrievedChunk> Relevant, double? TopCosine, double MinCosine, int IndexedChunks)
{
    public bool BelowThreshold => Relevant.Count == 0;
}

/// <summary>Stable codes of the knowledge endpoints.</summary>
public static class KnowledgeErrorCodes
{
    public const string NotIndexed = "knowledge_not_indexed";
    public const string ReindexInProgress = "reindex_in_progress";
    public const string NoDocuments = "no_documents";
    public const string InvalidQuestion = "invalid_question";
}

/// <summary>
/// Hybrid retrieval (plan s6.4): the question is embedded once, the vector store returns the nearest chunks by cosine and FTS5 the
/// best keyword matches (bm25), and reciprocal rank fusion merges the two rankings into the top-k. A chunk can only be used to
/// answer when its cosine is at or above <see cref="RagOptions.MinCosine"/>: RRF scores are relative, so without that gate the
/// system would "find" something for any question.
/// </summary>
public sealed class KnowledgeSearchService(
    IVectorStore store,
    IEmbeddingClient embeddings,
    EmbeddingCache cache,
    IBosunDbContext db,
    IOptions<RagOptions> options)
{
    public const string QueryFeature = "rag.query";

    public static string Normalise(string query) => string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public async Task<RetrievalResult> SearchAsync(int projectId, string query, int? k, CancellationToken ct)
    {
        var settings = options.Value;
        query = Normalise(query);
        if (query.Length == 0 || query.Length > settings.MaxQuestionChars || query.Any(char.IsControl))
        {
            throw new AppException(ErrorKind.Validation, KnowledgeErrorCodes.InvalidQuestion, $"The question must be 1 to {settings.MaxQuestionChars} characters without control characters.");
        }

        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw AppException.NotFound("Project", projectId);
        }

        var indexed = await db.KnowledgeChunks.CountAsync(c => c.ProjectId == projectId && c.Embedding != null, ct);
        if (indexed == 0)
        {
            throw new AppException(ErrorKind.Conflict, KnowledgeErrorCodes.NotIndexed, "The project documents are not indexed yet. Run the reindex first (POST /api/projects/{id}/knowledge/reindex or pnpm run kb:index).");
        }

        var take = Math.Clamp(k ?? settings.TopK, 1, 20);
        var vector = await EmbedQueryAsync(query, ct);
        var byVector = await store.SearchByVectorAsync(projectId, vector, settings.Candidates, ct);
        var byKeyword = await store.SearchByKeywordAsync(projectId, query, settings.Candidates, ct);

        var fused = Rrf.Fuse([byVector.Select(h => h.Id).ToList(), byKeyword.Select(h => h.Id).ToList()], [settings.VectorWeight, settings.KeywordWeight], settings.RrfK);
        var all = byVector.Concat(byKeyword).GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First());
        var chunks = fused.Take(take).Select(f =>
        {
            var hit = all[f.Id];
            var vectorIndex = IndexOf(byVector, f.Id);
            var keywordIndex = IndexOf(byKeyword, f.Id);
            double? cosine = vectorIndex is null ? null : byVector[vectorIndex.Value].Score;
            double? bm25 = keywordIndex is null ? null : byKeyword[keywordIndex.Value].Score;
            return new RetrievedChunk(
                hit.Id, hit.Source, hit.Section, hit.Page, hit.Text, f.Score, cosine, bm25,
                vectorIndex + 1, keywordIndex + 1, cosine >= settings.MinCosine);
        }).ToList();

        return new RetrievalResult(query, chunks, chunks.Where(c => c.AboveThreshold).ToList(), byVector.Count == 0 ? null : byVector[0].Score, settings.MinCosine, indexed);
    }

    private static int? IndexOf(IReadOnlyList<ChunkHit> hits, int id)
    {
        for (var i = 0; i < hits.Count; i++)
        {
            if (hits[i].Id == id)
            {
                return i;
            }
        }

        return null;
    }

    private async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        var key = EmbeddingCache.KeyOf(embeddings.Model, embeddings.Dimensions, nameof(EmbeddingTask.Query), EmbeddingCache.HashOf(query));
        if (cache.TryGet(key) is { } cached && cached.Length == embeddings.Dimensions)
        {
            return cached;
        }

        var result = await embeddings.EmbedAsync([query], EmbeddingTask.Query, QueryFeature, ct);
        var vector = VectorMath.L2Normalize(result.Vectors[0]);
        cache.Put([(key, vector)]);
        return vector;
    }
}
