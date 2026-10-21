using Bosun.Application.Llm;
using Bosun.Application.Rag;
using Microsoft.Extensions.Options;

namespace Bosun.Rag.Tests;

public sealed class EmbeddingCacheTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("bosun-cache-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private EmbeddingCache Make(string? path, int max = 2000, int queries = 256) =>
        new(Options.Create(new RagOptions { CachePath = path, CacheMaxEntries = max, QueryCacheEntries = queries }));

    private static string Key(string text) => EmbeddingCache.KeyOf("m", 3, nameof(EmbeddingTask.Document), EmbeddingCache.HashOf(text));

    [Fact]
    public void Query_vectors_stay_in_memory_and_are_never_written_to_the_file()
    {
        var path = Path.Combine(dir, "c.json");
        var cache = Make(path);
        var queryKey = EmbeddingCache.KeyOf("m", 3, nameof(EmbeddingTask.Query), EmbeddingCache.HashOf("secret question"));
        cache.PutQuery(queryKey, [1f, 2f, 3f]);
        cache.Put([(Key("doc"), new[] { 1f, 0f, 0f })]);

        cache.TryGetQuery(queryKey).Should().Equal(1f, 2f, 3f);
        File.ReadAllText(path).Should().NotContain(EmbeddingCache.HashOf("secret question")).And.NotContain("Query");
        Make(path).TryGetQuery(queryKey).Should().BeNull("a new process starts with no question vectors");
    }

    [Fact]
    public void Query_cache_is_a_bounded_lru()
    {
        var cache = Make(null, queries: 2);
        cache.PutQuery("a", [1f]);
        cache.PutQuery("b", [2f]);
        cache.TryGetQuery("a"); // a is now the most recently used
        cache.PutQuery("c", [3f]);

        cache.QueryCount.Should().Be(2);
        cache.TryGetQuery("b").Should().BeNull();
        cache.TryGetQuery("a").Should().NotBeNull();
        cache.TryGetQuery("c").Should().NotBeNull();
    }

    [Fact]
    public void Persisted_document_entries_are_capped_dropping_the_oldest()
    {
        var path = Path.Combine(dir, "c.json");
        var cache = Make(path, max: 3);
        for (var i = 0; i < 5; i++)
        {
            cache.Put([(Key("t" + i), new[] { (float)i, 0f, 0f })]);
        }

        cache.PersistedCount.Should().Be(3);
        var reloaded = Make(path, max: 3);
        reloaded.TryGet(Key("t0")).Should().BeNull();
        reloaded.TryGet(Key("t4")).Should().Equal(4f, 0f, 0f);
    }

    [Fact]
    public void Old_file_entries_written_for_queries_are_dropped_on_load()
    {
        var path = Path.Combine(dir, "old.json");
        var queryKey = EmbeddingCache.KeyOf("m", 3, nameof(EmbeddingTask.Query), "abc");
        File.WriteAllText(path, $"{{\"{queryKey}\":\"AAAAAAAAAAAAAAAA\"}}");

        Make(path).PersistedCount.Should().Be(0);
    }

    [Fact]
    public void A_write_failure_is_swallowed_and_leaves_no_temp_file()
    {
        // The cache path is an existing directory, so moving the temp file over it fails with an IO error.
        var path = Path.Combine(dir, "blocked");
        Directory.CreateDirectory(path);
        var cache = Make(path);

        var act = () => cache.Put([(Key("x"), new[] { 1f, 2f, 3f })]);

        act.Should().NotThrow();
        cache.TryGet(Key("x")).Should().Equal(1f, 2f, 3f);
        Directory.GetFiles(dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Concurrent_writers_use_distinct_temp_files_and_leave_a_valid_file()
    {
        var path = Path.Combine(dir, "c.json");
        var caches = Enumerable.Range(0, 4).Select(_ => Make(path)).ToList();

        Parallel.For(0, 40, i => caches[i % 4].Put([(Key("p" + i), new[] { (float)i, 1f, 2f })]));

        Directory.GetFiles(dir, "*.tmp").Should().BeEmpty();
        Make(path).PersistedCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_damaged_entry_is_a_miss_not_an_exception()
    {
        var path = Path.Combine(dir, "bad.json");
        File.WriteAllText(path, $"{{\"{Key("a")}\":\"!!!not base64!!!\",\"{Key("b")}\":\"AAAA\"}}");
        var cache = Make(path);

        cache.TryGet(Key("a")).Should().BeNull();
        cache.TryGet(Key("b")).Should().BeNull("3 bytes is not a whole number of floats");
        Make(Path.Combine(dir, "garbage.json")).TryGet("k").Should().BeNull();
        File.WriteAllText(Path.Combine(dir, "garbage2.json"), "{{{ nope");
        Make(Path.Combine(dir, "garbage2.json")).PersistedCount.Should().Be(0);
    }
}
