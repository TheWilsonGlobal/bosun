using System.Runtime.InteropServices;
using Bosun.Application;
using Bosun.Application.Rag;
using Bosun.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bosun.Infrastructure.Persistence.Sqlite;

/// <summary>
/// Vector + keyword retrieval on SQLite (plan s5.1). Embeddings are float32[768] BLOBs in <c>knowledge_chunks</c>; the vector search
/// reads the project's vectors and takes dot products in process (the vectors are L2-normalised, so the dot product IS the cosine).
/// At demo size (tens to a few hundred chunks) that is a few milliseconds. The keyword search is FTS5 <c>bm25()</c> on
/// <c>knowledge_fts</c>, kept in sync by triggers. Every SQL value goes in as a parameter.
/// </summary>
public sealed class SqliteVectorStore(BosunDbContext db, TimeProvider clock, WriteGate gate) : IVectorStore
{
    private sealed record KeywordRow(int Id, double Score);

    public async Task<IReadOnlyList<StoredChunk>> ListAsync(int projectId, CancellationToken ct) =>
        await db.KnowledgeChunks.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.Source).ThenBy(c => c.Ordinal)
            .Select(c => new StoredChunk(c.Id, c.Source, c.Section, c.Page, c.Ordinal, c.ContentHash, c.Embedding != null, c.EmbeddingModel, c.IndexedAt))
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<string, float[]>> GetEmbeddingsAsync(int projectId, string model, CancellationToken ct)
    {
        var rows = await db.KnowledgeChunks.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Embedding != null && c.EmbeddingModel == model)
            .Select(c => new { c.ContentHash, c.Embedding })
            .ToListAsync(ct);
        var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result[row.ContentHash] = Decode(row.Embedding!);
        }

        return result;
    }

    public async Task ReplaceAllAsync(int projectId, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct)
    {
        // Same single-writer gate as change-set applies and the alarm writer: a reindex must not interleave with them.
        using var held = await gate.EnterAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.KnowledgeChunks.Where(c => c.ProjectId == projectId).ExecuteDeleteAsync(ct);
        db.KnowledgeChunks.AddRange(chunks.Select(c => new KnowledgeChunk
        {
            ProjectId = projectId,
            Source = c.Source,
            Section = c.Section,
            Page = c.Page,
            Ordinal = c.Ordinal,
            Text = c.Text,
            ContentHash = c.ContentHash,
            Embedding = c.Embedding is null ? null : Encode(c.Embedding),
            EmbeddingModel = c.EmbeddingModel,
            IndexedAt = now,
            IsExclusion = c.IsExclusion,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<ChunkHit>> SearchByVectorAsync(int projectId, float[] query, int k, CancellationToken ct)
    {
        var rows = await db.KnowledgeChunks.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Embedding != null)
            .Select(c => new { c.Id, c.Source, c.Section, c.Page, c.Ordinal, c.Text, c.Embedding, c.IsExclusion })
            .ToListAsync(ct);
        return rows
            .Select(r => (Row: r, Vector: Decode(r.Embedding!)))
            .Where(x => x.Vector.Length == query.Length)
            .Select(x => new ChunkHit(x.Row.Id, x.Row.Source, x.Row.Section, x.Row.Page, x.Row.Ordinal, x.Row.Text, Dot(query, x.Vector), x.Row.IsExclusion))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Id)
            .Take(Math.Max(1, k))
            .ToList();
    }

    public async Task<IReadOnlyList<ChunkHit>> SearchByKeywordAsync(int projectId, string query, int k, CancellationToken ct)
    {
        var terms = KeywordTerms.Extract(query);
        if (terms.Count == 0)
        {
            return [];
        }

        // Every word is a quoted FTS5 string, so characters such as '-' or '"' in a question are data, never query syntax.
        var match = string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
        var limit = Math.Max(1, k);
        var rows = await db.Database
            .SqlQuery<KeywordRow>($"SELECT c.id AS Id, -bm25(knowledge_fts) AS Score FROM knowledge_fts JOIN knowledge_chunks c ON c.id = knowledge_fts.rowid WHERE knowledge_fts MATCH {match} AND c.project_id = {projectId} ORDER BY bm25(knowledge_fts), c.id LIMIT {limit}")
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.Id).ToList();
        var chunks = await db.KnowledgeChunks.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        // A concurrent reindex can delete a chunk between the FTS query and this read: skip it instead of throwing.
        var hits = new List<ChunkHit>(rows.Count);
        foreach (var row in rows)
        {
            if (chunks.TryGetValue(row.Id, out var chunk))
            {
                hits.Add(new ChunkHit(chunk.Id, chunk.Source, chunk.Section, chunk.Page, chunk.Ordinal, chunk.Text, row.Score, chunk.IsExclusion));
            }
        }

        return hits;
    }

    public static byte[] Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        MemoryMarshal.AsBytes(vector.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    public static float[] Decode(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, vector.Length * sizeof(float))).CopyTo(vector);
        return vector;
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }
}
