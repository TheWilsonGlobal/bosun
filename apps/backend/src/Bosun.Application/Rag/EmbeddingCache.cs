using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bosun.Application.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bosun.Application.Rag;

/// <summary>
/// Embedding reuse in two parts, because document text and user questions have different risks.
/// <para>
/// DOCUMENT vectors are kept in a JSON file keyed by "{model}|{dims}|{task}|{sha256 of the embedded text}". It exists because the
/// embedding free tier is 1,000 requests a day: a reseed (<c>db:reset</c> deletes the database) or a reindex of unchanged documents
/// must cost nothing. The file holds at most <see cref="RagOptions.CacheMaxEntries"/> vectors (the oldest are dropped first), holds
/// document text vectors only, and no keys or secrets. Writes go to a uniquely named temp file that is then moved over the real
/// one; a failed write (disk full, locked, read-only) is logged and swallowed, because the cache is an optimisation and must
/// never turn a search or an index run into an error. A damaged file or entry is ignored.
/// </para>
/// <para>
/// QUERY vectors (what people typed) are NOT persisted: a question can contain anything, and a file of them would outlive the
/// session. They live in a small in-memory LRU (<see cref="RagOptions.QueryCacheEntries"/>) that disappears with the process.
/// Entries of the old file format that were written for queries are dropped when the file is loaded.
/// </para>
/// Safe for concurrent use inside one process.
/// </summary>
public sealed class EmbeddingCache(IOptions<RagOptions> options, ILogger<EmbeddingCache>? logger = null)
{
    private const string QueryMarker = "|" + nameof(EmbeddingTask.Query) + "|";

    private readonly object gate = new();
    private readonly LinkedList<string> queryOrder = new();
    private readonly Dictionary<string, (LinkedListNode<string> Node, float[] Vector)> queries = new(StringComparer.Ordinal);
    private System.Collections.Generic.OrderedDictionary<string, string>? entries;

    private string? Path => string.IsNullOrWhiteSpace(options.Value.CachePath) ? null : options.Value.CachePath;

    public static string HashOf(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string KeyOf(string model, int dimensions, string task, string hash) => $"{model}|{dimensions}|{task}|{hash}";

    // ---------- document vectors (persisted, capped) ----------

    public float[]? TryGet(string key)
    {
        lock (gate)
        {
            var loaded = Load();
            if (!loaded.TryGetValue(key, out var base64))
            {
                return null;
            }

            if (TryDecode(base64) is { } vector)
            {
                return vector;
            }

            loaded.Remove(key); // a damaged entry is only a cache miss
            return null;
        }
    }

    public void Put(IEnumerable<(string Key, float[] Vector)> items)
    {
        lock (gate)
        {
            var loaded = Load();
            foreach (var (key, vector) in items)
            {
                loaded.Remove(key); // re-adding moves it to the newest end
                loaded[key] = Convert.ToBase64String(MemoryMarshal.AsBytes(vector.AsSpan()));
            }

            Trim(loaded);
            Save(loaded);
        }
    }

    /// <summary>Documents in the file right now.</summary>
    public int PersistedCount
    {
        get
        {
            lock (gate)
            {
                return Load().Count;
            }
        }
    }

    // ---------- query vectors (memory only, LRU) ----------

    public float[]? TryGetQuery(string key)
    {
        lock (gate)
        {
            if (!queries.TryGetValue(key, out var hit))
            {
                return null;
            }

            queryOrder.Remove(hit.Node);
            queryOrder.AddFirst(hit.Node);
            return hit.Vector;
        }
    }

    public void PutQuery(string key, float[] vector)
    {
        lock (gate)
        {
            if (queries.Remove(key, out var old))
            {
                queryOrder.Remove(old.Node);
            }

            queries[key] = (queryOrder.AddFirst(key), vector);
            var cap = Math.Max(1, options.Value.QueryCacheEntries);
            while (queries.Count > cap && queryOrder.Last is { } last)
            {
                queries.Remove(last.Value);
                queryOrder.RemoveLast();
            }
        }
    }

    public int QueryCount
    {
        get
        {
            lock (gate)
            {
                return queries.Count;
            }
        }
    }

    // ---------- file ----------

    private void Trim(System.Collections.Generic.OrderedDictionary<string, string> loaded)
    {
        var cap = Math.Max(1, options.Value.CacheMaxEntries);
        while (loaded.Count > cap)
        {
            loaded.RemoveAt(0);
        }
    }

    private void Save(System.Collections.Generic.OrderedDictionary<string, string> loaded)
    {
        if (Path is not { } path)
        {
            return;
        }

        var temp = path + "." + Guid.NewGuid().ToString("N")[..12] + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            File.WriteAllText(temp, JsonSerializer.Serialize(loaded));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger?.LogWarning(ex, "The embedding cache file could not be written; continuing without persisting");
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // best effort: a stray temp file is harmless
            }
        }
    }

    private System.Collections.Generic.OrderedDictionary<string, string> Load()
    {
        if (entries is not null)
        {
            return entries;
        }

        entries = new System.Collections.Generic.OrderedDictionary<string, string>(StringComparer.Ordinal);
        if (Path is { } path && File.Exists(path))
        {
            try
            {
                var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                foreach (var (key, value) in read ?? [])
                {
                    if (!key.Contains(QueryMarker, StringComparison.Ordinal))
                    {
                        entries[key] = value;
                    }
                }

                Trim(entries);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged or unreadable cache is only a cache: start empty and rewrite it on the next Put.
                logger?.LogWarning(ex, "The embedding cache file could not be read; starting empty");
            }
        }

        return entries;
    }

    private static float[]? TryDecode(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
            {
                return null;
            }

            var vector = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            return vector;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
