using System.Text.Json.Nodes;
using Bosun.Agent;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Application.Rag;
using Bosun.Domain.Entities;
using Bosun.Infrastructure.Configuration;
using Bosun.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Rag.Tests;

/// <summary>
/// A deterministic stand-in for the embedding provider: every content word hashes to one of <see cref="Slots"/> slots, so texts
/// that share words have a high cosine and unrelated texts have about zero. Counts what it was asked to embed.
/// </summary>
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public string Model => "fake-embedding";

    public int Dimensions => Slots;

    public const int Slots = 1024;

    public List<(EmbeddingTask Task, string Feature, int Texts)> Calls { get; } = [];

    public int TextsEmbedded => Calls.Sum(c => c.Texts);

    public Exception? Fail { get; set; }

    public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingTask task, string feature, CancellationToken cancellationToken)
    {
        if (Fail is not null)
        {
            throw Fail;
        }

        Calls.Add((task, feature, texts.Count));
        return Task.FromResult(new EmbeddingResult(texts.Select(Vectorise).ToList(), Dimensions, Model, texts.Sum(t => t.Length / 4), TimeSpan.FromMilliseconds(1), 1));
    }

    public static float[] Vectorise(string text)
    {
        var vector = new float[Slots];
        foreach (var word in KeywordTerms.Extract(text, 500))
        {
            var slot = (int)((uint)word.Aggregate(17, (h, c) => (h * 31) + c) % Slots);
            vector[slot] += 1;
        }

        return VectorMath.L2Normalize(vector);
    }
}

/// <summary>A scripted model: each call pops the next reply; running out of script fails loudly.</summary>
public sealed class ScriptedLlm : ILlmClient
{
    private readonly Queue<Func<LlmRequest, LlmResponse>> script = new();

    public List<LlmRequest> Requests { get; } = [];

    public string Provider => "scripted";

    public string DefaultModel => "scripted-model";

    public int DefaultMaxOutputTokens => 8192;

    public int MaxOutputTokensCap => 32768;

    public ScriptedLlm Reply(string json) => Then(_ => Response(json));

    public ScriptedLlm Then(Func<LlmRequest, LlmResponse> reply)
    {
        script.Enqueue(reply);
        return this;
    }

    public static LlmResponse Response(string text) => new()
    {
        Model = "scripted-model",
        Parts = [new TextPart(text)],
        FinishReason = LlmFinishReason.Stop,
        FinishReasonRaw = "STOP",
        Usage = new LlmUsage(100, 20, 0, 120),
        Latency = TimeSpan.FromMilliseconds(5),
    };

    public static string Json(bool answered, string answer, params (string Source, string Section, int Page)[] citations) =>
        new JsonObject
        {
            ["answer"] = answer,
            ["answered"] = answered,
            ["citations"] = new JsonArray(citations.Select(c => (JsonNode)new JsonObject { ["source"] = c.Source, ["section"] = c.Section, ["page"] = c.Page }).ToArray()),
        }.ToJsonString();

    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return script.TryDequeue(out var reply)
            ? Task.FromResult(reply(request))
            : throw new InvalidOperationException($"The scripted model has no reply left for call #{Requests.Count} ({request.Feature}).");
    }
}

/// <summary>A real SQLite file (migrated, one project), a docs folder and an embedding cache file of its own, with fake clients.</summary>
public sealed class RagHost : IDisposable
{
    private readonly ServiceProvider provider;

    public string Dir { get; }

    public string DocsDir { get; }

    public string DbPath { get; }

    public string CachePath { get; }

    public FakeEmbeddingClient Embeddings { get; } = new();

    public ScriptedLlm Llm { get; } = new();

    public RagHost(Action<string>? writeDocs = null, bool useCacheFile = true, string? dir = null, double? minCosine = null)
    {
        Dir = dir ?? Path.Combine(Path.GetTempPath(), "bosun-rag-tests", "t-" + Guid.NewGuid().ToString("N")[..8]);
        DocsDir = Path.Combine(Dir, "docs");
        Directory.CreateDirectory(DocsDir);
        DbPath = Path.Combine(Dir, "bosun.db");
        CachePath = Path.Combine(Dir, "cache.json");
        if (writeDocs is null)
        {
            CopySeedDocs(DocsDir);
        }
        else
        {
            writeDocs(DocsDir);
        }

        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "sqlite",
            ["Database:ConnectionString"] = $"Data Source={DbPath}",
            ["Rag:DocsPath"] = DocsDir,
            ["Rag:CachePath"] = useCacheFile ? CachePath : "",
        };

        // The fake embedding is a 64-slot bag of words, so cosines are far lower than a real model's: use a matching threshold.
        settings["Rag:MinCosine"] = (minCosine ?? 0.12).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddBosunDatabase();
        services.AddBosunApplication();
        services.AddBosunAgent();
        services.AddSingleton<IEmbeddingClient>(Embeddings);
        services.AddSingleton<ILlmClient>(Llm);
        services.AddSingleton<StructuredLlm>();
        provider = services.BuildServiceProvider();

        DatabaseInitializer.MigrateAsync(provider).GetAwaiter().GetResult();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        if (!db.Projects.Any())
        {
            db.Vessels.Add(new Vessel { Name = "Test vessel", Projects = [new Project { Name = "Test project", CreatedAt = DateTime.UtcNow }] });
            db.SaveChanges();
        }
    }

    public static string SeedDocsDirectory
    {
        get
        {
            var root = RepoRoot.TryFind(AppContext.BaseDirectory) ?? throw new InvalidOperationException("Bosun.sln not found above the test binaries.");
            return Path.Combine(root, "seed", "docs");
        }
    }

    public static void CopySeedDocs(string target)
    {
        foreach (var file in Directory.EnumerateFiles(SeedDocsDirectory, "*.md"))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    public AsyncServiceScope Scope() => provider.CreateAsyncScope();

    public T Get<T>()
        where T : notnull => provider.GetRequiredService<T>();

    public void Dispose()
    {
        provider.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
            // temp files outside the repo
        }
    }
}
