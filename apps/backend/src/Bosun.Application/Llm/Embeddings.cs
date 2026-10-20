namespace Bosun.Application.Llm;

public enum EmbeddingTask
{
    /// <summary>A passage that will be searched (RAG chunks).</summary>
    Document,

    /// <summary>A search question.</summary>
    Query,
}

/// <param name="Vectors">One L2-normalised vector per input text, in input order.</param>
/// <param name="PromptTokens">Input tokens the provider reported (0 when it reports none).</param>
public sealed record EmbeddingResult(IReadOnlyList<float[]> Vectors, int Dimensions, string Model, int PromptTokens, TimeSpan Latency, int Attempts);

/// <summary>
/// Text embeddings for RAG (Phase 6). Vectors come back at EMBEDDING_DIMS (768: under the pgvector HNSW 2,000-dim limit,
/// so the Phase 8 switch needs no re-embedding) and L2-normalised, so cosine similarity is a plain dot product.
/// </summary>
public interface IEmbeddingClient
{
    string Model { get; }
    int Dimensions { get; }

    /// <summary>Embeds all texts, batching as the provider allows. Empty input returns an empty result without a call.</summary>
    /// <exception cref="LlmException">Any provider failure, mapped to a stable code.</exception>
    /// <param name="feature">Product feature making the call ("rag.index", "rag.query" ...), logged to llm_calls.</param>
    Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingTask task, string feature, CancellationToken cancellationToken);
}

public static class VectorMath
{
    /// <summary>Scales to unit length in place (providers normalise only some dimensionalities). A zero vector is left unchanged.</summary>
    public static float[] L2Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var x in vector)
        {
            sum += (double)x * x;
        }

        var norm = Math.Sqrt(sum);
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return vector;
    }

    public static double Norm(IReadOnlyList<float> vector)
    {
        double sum = 0;
        foreach (var x in vector)
        {
            sum += (double)x * x;
        }

        return Math.Sqrt(sum);
    }
}
