using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bosun.Application.Rag;

/// <summary>
/// A small JSON file of embeddings keyed by "{model}|{dims}|{task}|{sha256 of the embedded text}". It exists because the embedding
/// free tier is 1,000 requests a day: a reseed (<c>db:reset</c> deletes the database) or a reindex of unchanged documents, and a
/// repeated eval question, must cost nothing. It holds vectors of document text and questions only; no keys, no secrets.
/// Safe for concurrent use inside one process.
/// </summary>
public sealed class EmbeddingCache(IOptions<RagOptions> options)
{
    private readonly object gate = new();
    private Dictionary<string, string>? entries;

    private string? Path => string.IsNullOrWhiteSpace(options.Value.CachePath) ? null : options.Value.CachePath;

    public static string HashOf(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string KeyOf(string model, int dimensions, string task, string hash) => $"{model}|{dimensions}|{task}|{hash}";

    public float[]? TryGet(string key)
    {
        lock (gate)
        {
            var loaded = Load();
            return loaded.TryGetValue(key, out var base64) ? Decode(base64) : null;
        }
    }

    public void Put(IEnumerable<(string Key, float[] Vector)> items)
    {
        lock (gate)
        {
            var loaded = Load();
            foreach (var (key, vector) in items)
            {
                loaded[key] = Convert.ToBase64String(MemoryMarshal.AsBytes(vector.AsSpan()));
            }

            if (Path is { } path)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(loaded));
                File.Move(temp, path, overwrite: true);
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        if (entries is not null)
        {
            return entries;
        }

        entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Path is { } path && File.Exists(path))
        {
            try
            {
                entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? entries;
            }
            catch (JsonException)
            {
                // A damaged cache is only a cache: start empty and rewrite it on the next Put.
            }
        }

        return entries;
    }

    private static float[] Decode(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, vector.Length * sizeof(float));
        return vector;
    }
}
