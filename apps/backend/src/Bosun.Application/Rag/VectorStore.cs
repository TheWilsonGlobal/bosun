namespace Bosun.Application.Rag;

/// <summary>A chunk to store; <see cref="Embedding"/> is L2-normalised, <see cref="ContentHash"/> hashes the embedded text.</summary>
public sealed record ChunkRecord(string Source, string Section, int Page, int Ordinal, string Text, string ContentHash, float[]? Embedding, string? EmbeddingModel);

public sealed record StoredChunk(int Id, string Source, string Section, int Page, int Ordinal, string ContentHash, bool HasEmbedding, string? EmbeddingModel, DateTime IndexedAt);

/// <param name="Score">Cosine similarity for vector search; a positive keyword score (higher is better, that is -bm25) for keyword search.</param>
public sealed record ChunkHit(int Id, string Source, string Section, int Page, int Ordinal, string Text, double Score);

/// <summary>
/// Provider-neutral storage and retrieval of knowledge chunks (plan s5.1). SQLite: <c>SqliteVectorStore</c> (BLOB + in-process
/// cosine, FTS5 bm25). PostgreSQL (Phase 8): <c>PgVectorStore</c> (vector(768) + HNSW, tsvector/ts_rank). Nothing above this
/// interface knows which one is underneath.
/// </summary>
public interface IVectorStore
{
    Task<IReadOnlyList<StoredChunk>> ListAsync(int projectId, CancellationToken ct);

    /// <summary>Embeddings already stored, by content hash, for one embedding model (so unchanged text is never embedded again).</summary>
    Task<IReadOnlyDictionary<string, float[]>> GetEmbeddingsAsync(int projectId, string model, CancellationToken ct);

    /// <summary>Replaces the project's chunks with <paramref name="chunks"/> in one transaction (all or nothing).</summary>
    Task ReplaceAllAsync(int projectId, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct);

    /// <summary>Top <paramref name="k"/> by cosine similarity to <paramref name="query"/> (L2-normalised). Chunks without an embedding are skipped.</summary>
    Task<IReadOnlyList<ChunkHit>> SearchByVectorAsync(int projectId, float[] query, int k, CancellationToken ct);

    /// <summary>Top <paramref name="k"/> by keyword relevance (bm25 over chunk text and section title). Empty when the query has no usable words.</summary>
    Task<IReadOnlyList<ChunkHit>> SearchByKeywordAsync(int projectId, string query, int k, CancellationToken ct);
}
