namespace Bosun.Application.Rag;

/// <summary>
/// "Rag" configuration section (all optional; the defaults below are what the eval and the demo use). The paths are absolute
/// after the Infrastructure registration has resolved them against the repo root.
/// </summary>
public sealed class RagOptions
{
    /// <summary>Folder with the project documents (*.md). Default: seed/docs under the repo root.</summary>
    public string? DocsPath { get; set; }

    /// <summary>
    /// JSON file that remembers embeddings by content hash, so reseeding or reindexing identical text never calls the provider again
    /// (the free tier allows 1,000 embedding requests a day). Default: data/rag-embedding-cache.json. Holds vectors only, no keys.
    /// Null or empty = no file cache.
    /// </summary>
    public string? CachePath { get; set; }

    /// <summary>Most document vectors kept in the cache file (the oldest go first). The seed set is a few dozen; this is a safety cap.</summary>
    public int CacheMaxEntries { get; set; } = 2000;

    /// <summary>Question vectors kept in memory (LRU). Questions are never written to disk.</summary>
    public int QueryCacheEntries { get; set; } = 256;

    /// <summary>Chunks returned to the caller (plan: top-k = 6).</summary>
    public int TopK { get; set; } = 6;

    /// <summary>Candidates taken from EACH retriever before fusion.</summary>
    public int Candidates { get; set; } = 20;

    /// <summary>Reciprocal rank fusion constant (the usual 60).</summary>
    public int RrfK { get; set; } = 60;

    public double VectorWeight { get; set; } = 1.0;

    public double KeywordWeight { get; set; } = 1.0;

    /// <summary>
    /// (0.62: calibrated live on 12 probe questions that are NOT eval cases. On-topic questions scored 0.66 to 0.77, clearly off-domain ones 0.53 to 0.57, with gemini-embedding-2 at 768 dims.)
    /// Relevance threshold on the cosine similarity between a chunk and the question. RRF scores are rank-based (a bad question still
    /// has a "best" chunk), so only cosine says whether anything is actually close. No chunk at or above it means a refusal.
    /// </summary>
    public double MinCosine { get; set; } = 0.62;

    public int MaxQuestionChars { get; set; } = 500;
}
