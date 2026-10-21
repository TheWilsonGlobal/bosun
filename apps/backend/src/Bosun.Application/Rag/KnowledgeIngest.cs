using Bosun.Application.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bosun.Application.Rag;

/// <param name="Embedded">Chunks whose vector had to be requested from the provider this time.</param>
/// <param name="FromStore">Chunks whose vector was kept from the previous index (same content hash).</param>
/// <param name="FromCache">Chunks whose vector came from the embedding cache file.</param>
/// <param name="EmbeddingRequests">HTTP requests made to the embedding provider (retries included).</param>
/// <param name="Skipped">Cache-only mode and at least one chunk was not cached: nothing was changed and no request was made.</param>
public sealed record IndexReport(
    int Documents,
    int Chunks,
    int Embedded,
    int FromStore,
    int FromCache,
    int EmbeddingRequests,
    int PromptTokens,
    bool Skipped,
    string Model,
    int Dimensions,
    long ElapsedMs);

public sealed record KnowledgeDocumentStatus(string Source, int Chunks, int Pages);

/// <param name="Indexed">At least one chunk, and every chunk has an embedding.</param>
/// <param name="DocumentsOnDisk">Markdown files currently in the docs folder (a difference to Documents means "reindex").</param>
public sealed record KnowledgeStatus(
    bool Indexed,
    int Documents,
    int Chunks,
    int EmbeddedChunks,
    int DocumentsOnDisk,
    string? EmbeddingModel,
    int Dimensions,
    DateTime? LastIndexedAt,
    IReadOnlyList<KnowledgeDocumentStatus> Sources);

/// <summary>Only one reindex runs at a time (it spends embedding quota and replaces the whole index).</summary>
public sealed class KnowledgeIndexGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public bool TryEnter() => gate.Wait(0);

    public void Exit() => gate.Release();
}

/// <summary>
/// Chunks the Markdown files of the docs folder, embeds only what is new, and swaps the project's index in one transaction. A chunk
/// is "unchanged" when the hash of its embedded text (source, section and text) is unchanged; its vector then comes from the
/// previous index, or from the embedding cache file, and the provider is not called. So a second reindex costs no request, and
/// reseeding the database (which deletes it) costs none either as long as the cache file is there.
/// </summary>
public sealed class KnowledgeIngestService(
    IVectorStore store,
    IEmbeddingClient embeddings,
    EmbeddingCache cache,
    IBosunDbContext db,
    KnowledgeIndexGate gate,
    IOptions<RagOptions> options)
{
    public const string IndexFeature = "rag.index";
    public const int BatchSize = 100;

    /// <summary>The exact text that is embedded for a chunk: the section title gives the passage its context.</summary>
    public static string EmbeddingInput(DocumentChunk chunk) => $"{chunk.Source} - {chunk.Section}\n{chunk.Text}";

    public IReadOnlyList<DocumentChunk> LoadChunks()
    {
        var folder = options.Value.DocsPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        var chunks = new List<DocumentChunk>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            chunks.AddRange(Chunker.Chunk(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)));
        }

        return chunks;
    }

    /// <param name="cacheOnly">Never call the provider: index only when every chunk is already known (used by the database seed).</param>
    public async Task<IndexReport> ReindexAsync(int projectId, bool cacheOnly, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw AppException.NotFound("Project", projectId);
        }

        var chunks = LoadChunks();
        if (chunks.Count == 0)
        {
            throw new AppException(ErrorKind.Unprocessable, KnowledgeErrorCodes.NoDocuments, "There are no Markdown documents to index in the docs folder.");
        }

        if (!gate.TryEnter())
        {
            throw new AppException(ErrorKind.Conflict, KnowledgeErrorCodes.ReindexInProgress, "A reindex is already running. Wait for it to finish.");
        }

        try
        {
            return await RunAsync(projectId, chunks, cacheOnly, ct);
        }
        finally
        {
            gate.Exit();
        }
    }

    private async Task<IndexReport> RunAsync(int projectId, IReadOnlyList<DocumentChunk> chunks, bool cacheOnly, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var model = embeddings.Model;
        var dimensions = embeddings.Dimensions;
        var inputs = chunks.Select(c => (Chunk: c, Hash: EmbeddingCache.HashOf(EmbeddingInput(c)))).ToList();

        var known = await store.GetEmbeddingsAsync(projectId, model, ct);
        var vectors = new float[]?[inputs.Count];
        var fromStore = 0;
        var fromCache = 0;
        for (var i = 0; i < inputs.Count; i++)
        {
            if (known.TryGetValue(inputs[i].Hash, out var stored) && stored.Length == dimensions)
            {
                vectors[i] = stored;
                fromStore++;
            }
            else if (cache.TryGet(EmbeddingCache.KeyOf(model, dimensions, nameof(EmbeddingTask.Document), inputs[i].Hash)) is { } cached && cached.Length == dimensions)
            {
                vectors[i] = cached;
                fromCache++;
            }
        }

        var missing = Enumerable.Range(0, inputs.Count).Where(i => vectors[i] is null).ToList();
        if (cacheOnly && missing.Count > 0)
        {
            return new IndexReport(chunks.Select(c => c.Source).Distinct().Count(), chunks.Count, 0, 0, 0, 0, 0, true, model, dimensions, watch.ElapsedMilliseconds);
        }

        // Identical texts are embedded once.
        var requests = 0;
        var tokens = 0;
        var embedded = 0;
        var unique = missing.GroupBy(i => inputs[i].Hash).Select(g => g.First()).ToList();
        for (var offset = 0; offset < unique.Count; offset += BatchSize)
        {
            var batch = unique.Skip(offset).Take(BatchSize).ToList();
            var result = await embeddings.EmbedAsync(batch.Select(i => EmbeddingInput(inputs[i].Chunk)).ToList(), EmbeddingTask.Document, IndexFeature, ct);
            if (result.Vectors.Count != batch.Count || result.Vectors.Any(v => v.Length != dimensions))
            {
                throw new InvalidOperationException($"The embedding provider returned {result.Vectors.Count} vectors (expected {batch.Count}) or vectors that are not {dimensions}-dimensional.");
            }

            requests += result.Attempts;
            tokens += result.PromptTokens;
            embedded += batch.Count;
            var fresh = batch.Select((index, n) => (Key: EmbeddingCache.KeyOf(model, dimensions, nameof(EmbeddingTask.Document), inputs[index].Hash), Vector: VectorMath.L2Normalize(result.Vectors[n]))).ToList();
            cache.Put(fresh);
            foreach (var (index, n) in batch.Select((index, n) => (index, n)))
            {
                foreach (var same in missing.Where(m => inputs[m].Hash == inputs[index].Hash))
                {
                    vectors[same] = fresh[n].Vector;
                }
            }
        }

        var records = inputs.Select((x, i) => new ChunkRecord(
            x.Chunk.Source, x.Chunk.Section, x.Chunk.Page, x.Chunk.Ordinal, x.Chunk.Text, x.Hash, vectors[i], model)).ToList();
        await store.ReplaceAllAsync(projectId, records, ct);
        return new IndexReport(chunks.Select(c => c.Source).Distinct().Count(), chunks.Count, embedded, fromStore, fromCache, requests, tokens, false, model, dimensions, watch.ElapsedMilliseconds);
    }

    public async Task<KnowledgeStatus> StatusAsync(int projectId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw AppException.NotFound("Project", projectId);
        }

        var stored = await store.ListAsync(projectId, ct);
        var embeddedCount = stored.Count(c => c.HasEmbedding);
        var folder = options.Value.DocsPath;
        var onDisk = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.md").Count() : 0;
        var sources = stored.GroupBy(c => c.Source)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new KnowledgeDocumentStatus(g.Key, g.Count(), g.Select(c => c.Page).Distinct().Count()))
            .ToList();
        return new KnowledgeStatus(
            stored.Count > 0 && embeddedCount == stored.Count,
            sources.Count,
            stored.Count,
            embeddedCount,
            onDisk,
            stored.Select(c => c.EmbeddingModel).FirstOrDefault(m => m is not null),
            embeddings.Dimensions,
            stored.Count == 0 ? null : stored.Max(c => c.IndexedAt),
            sources);
    }
}
