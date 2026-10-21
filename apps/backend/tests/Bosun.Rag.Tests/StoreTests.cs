using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Application.Rag;
using Bosun.Domain.Entities;
using Bosun.Infrastructure.Persistence;
using Bosun.Infrastructure.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Rag.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly RagHost host = new(writeDocs: _ => { });

    public void Dispose() => host.Dispose();

    private static float[] Unit(params float[] values) => VectorMath.L2Normalize(values);

    private static ChunkRecord Chunk(int ordinal, string text, float[]? vector, string section = "S", string source = "doc") =>
        new(source, section, 1, ordinal, text, EmbeddingCache.HashOf(text), vector, "m");

    [Fact]
    public async Task Vector_search_returns_the_top_k_by_cosine_in_order_and_scopes_to_the_project()
    {
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        await store.ReplaceAllAsync(1,
        [
            Chunk(0, "north", Unit(1, 0, 0)),
            Chunk(1, "north-east", Unit(1, 1, 0)),
            Chunk(2, "east", Unit(0, 1, 0)),
            Chunk(3, "up", Unit(0, 0, 1)),
            Chunk(4, "no vector", null),
        ], CancellationToken.None);

        var hits = await store.SearchByVectorAsync(1, Unit(1, 0.2f, 0), 3, CancellationToken.None);

        hits.Select(h => h.Text).Should().Equal("north", "north-east", "east");
        hits[0].Score.Should().BeApproximately(1 / Math.Sqrt(1.04), 1e-6);
        hits.Select(h => h.Score).Should().BeInDescendingOrder();
        (await store.SearchByVectorAsync(1, Unit(0, 0, 1), 10, CancellationToken.None)).Should().HaveCount(4, "a chunk without an embedding is skipped");
        (await store.SearchByVectorAsync(2, Unit(1, 0, 0), 10, CancellationToken.None)).Should().BeEmpty("other projects see nothing");
        (await store.SearchByVectorAsync(1, [1f, 0f], 10, CancellationToken.None)).Should().BeEmpty("vectors of another dimension never match");
    }

    [Fact]
    public async Task Replace_all_waits_for_the_shared_write_gate()
    {
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var gate = scope.ServiceProvider.GetRequiredService<WriteGate>();

        Task replace;
        using (await gate.EnterAsync(CancellationToken.None))
        {
            replace = store.ReplaceAllAsync(1, [Chunk(0, "gated", Unit(1, 0, 0))], CancellationToken.None);
            await Task.Delay(300);
            replace.IsCompleted.Should().BeFalse("a change-set apply or the alarm writer holds the single-writer gate");
        }

        await replace.WaitAsync(TimeSpan.FromSeconds(10));
        (await store.ListAsync(1, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task Embeddings_round_trip_as_a_blob_and_replace_all_swaps_the_whole_index()
    {
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var vector = Unit(0.25f, -0.5f, 0.75f, 1f);
        SqliteVectorStore.Decode(SqliteVectorStore.Encode(vector)).Should().Equal(vector);

        await store.ReplaceAllAsync(1, [Chunk(0, "first", vector), Chunk(1, "second", vector)], CancellationToken.None);
        (await store.GetEmbeddingsAsync(1, "m", CancellationToken.None)).Should().ContainKey(EmbeddingCache.HashOf("first")).WhoseValue.Should().Equal(vector);
        (await store.GetEmbeddingsAsync(1, "other-model", CancellationToken.None)).Should().BeEmpty("vectors belong to the model that made them");

        await store.ReplaceAllAsync(1, [Chunk(0, "third", vector)], CancellationToken.None);
        (await store.ListAsync(1, CancellationToken.None)).Select(c => c.ContentHash).Should().Equal(EmbeddingCache.HashOf("third"));
    }

    [Fact]
    public async Task Keyword_search_ranks_by_bm25_stems_words_and_respects_the_section_title()
    {
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        await store.ReplaceAllAsync(1,
        [
            Chunk(0, "The delay of the exhaust alarm is ten seconds because of thermocouple lag.", null, section: "Exhaust delay"),
            Chunk(1, "Boiler water level trips the burner. Delay is five seconds.", null, section: "Boiler"),
            Chunk(2, "Hysteresis keeps an alarm from chattering. Nothing about timing here.", null, section: "Hysteresis"),
            Chunk(3, "Nothing relevant at all.", null, section: "Other"),
        ], CancellationToken.None);

        var hits = await store.SearchByKeywordAsync(1, "why is the exhaust delay ten seconds and not five?", 10, CancellationToken.None);

        hits.Select(h => h.Section).Should().StartWith("Exhaust delay");
        hits.Select(h => h.Section).Should().NotContain("Other");
        hits.Select(h => h.Score).Should().BeInDescendingOrder();
        hits.Should().OnlyContain(h => h.Score > 0);

        (await store.SearchByKeywordAsync(1, "delays", 10, CancellationToken.None)).Select(h => h.Section).Should().Contain(["Exhaust delay", "Boiler"], "the porter stemmer maps delays to delay");
        (await store.SearchByKeywordAsync(1, "chattering", 10, CancellationToken.None)).Should().ContainSingle().Which.Section.Should().Be("Hysteresis");
        (await store.SearchByKeywordAsync(1, "hysteresis", 10, CancellationToken.None)).Should().ContainSingle("the section title is indexed too").Which.Section.Should().Be("Hysteresis");
        (await store.SearchByKeywordAsync(2, "exhaust", 10, CancellationToken.None)).Should().BeEmpty("other projects see nothing");
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("OR AND NOT NEAR(")]
    [InlineData("ME-EXH-TT01 \"quoted\" (paren) * ^ : -")]
    [InlineData("a' OR 1=1; DROP TABLE knowledge_chunks; --")]
    [InlineData("the and of")]
    public async Task Hostile_or_empty_keyword_queries_are_data_not_fts_syntax(string query)
    {
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        await store.ReplaceAllAsync(1, [Chunk(0, "exhaust gas temperature alarm", null)], CancellationToken.None);

        var act = async () => await store.SearchByKeywordAsync(1, query, 5, CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await scope.ServiceProvider.GetRequiredService<IBosunDbContext>().KnowledgeChunks.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_fts_index_follows_inserts_deletes_and_updates_through_triggers()
    {
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        await store.ReplaceAllAsync(1, [Chunk(0, "alpha unique-word-zebra", null)], CancellationToken.None);
        (await store.SearchByKeywordAsync(1, "zebra", 5, CancellationToken.None)).Should().HaveCount(1);

        var chunk = await db.KnowledgeChunks.SingleAsync();
        chunk.Text = "now it talks about giraffe only";
        await db.SaveChangesAsync();
        (await store.SearchByKeywordAsync(1, "zebra", 5, CancellationToken.None)).Should().BeEmpty("the update trigger removed the old words");
        (await store.SearchByKeywordAsync(1, "giraffe", 5, CancellationToken.None)).Should().HaveCount(1);

        await store.ReplaceAllAsync(1, [], CancellationToken.None);
        (await store.SearchByKeywordAsync(1, "giraffe", 5, CancellationToken.None)).Should().BeEmpty("the delete trigger removed it");
    }

    [Fact]
    public void Keyword_terms_drop_stop_words_keep_numbers_and_are_distinct()
    {
        KeywordTerms.Extract("Why is the delay 10 s and not 5 for the exhaust, exhaust gas?").Should().Equal("delay", "10", "5", "exhaust", "gas");
        KeywordTerms.Extract("the of a").Should().BeEmpty();
    }

    [Fact]
    public async Task The_exclusion_flag_round_trips_through_the_store_and_into_search_hits()
    {
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        await store.ReplaceAllAsync(1,
        [
            Chunk(0, "real facts about delay", Unit(1, 0, 0)),
            Chunk(1, "delay is not covered here", Unit(0.9f, 0.1f, 0), section: "Scope") with { IsExclusion = true },
        ], CancellationToken.None);

        var vector = await store.SearchByVectorAsync(1, Unit(1, 0, 0), 5, CancellationToken.None);
        var keyword = await store.SearchByKeywordAsync(1, "delay", 5, CancellationToken.None);

        vector.Single(h => h.Section == "Scope").IsExclusion.Should().BeTrue();
        vector.Single(h => h.Section == "S").IsExclusion.Should().BeFalse();
        keyword.Single(h => h.Section == "Scope").IsExclusion.Should().BeTrue();
    }

    [Fact]
    public async Task Replace_all_waits_for_the_write_gate_like_every_other_writer()
    {
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var gate = host.Get<WriteGate>();

        Task replace;
        using (await gate.EnterAsync(CancellationToken.None))
        {
            replace = store.ReplaceAllAsync(1, [Chunk(0, "gated", Unit(1, 0, 0))], CancellationToken.None);
            await Task.Delay(300);
            replace.IsCompleted.Should().BeFalse("the gate is held by someone else");
        }

        await replace.WaitAsync(TimeSpan.FromSeconds(10));
        (await store.ListAsync(1, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_chunk_that_disappears_between_the_keyword_query_and_the_read_is_skipped_not_thrown()
    {
        await using var scope = host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        await store.ReplaceAllAsync(1, [Chunk(0, "alpha delay", Unit(1, 0, 0)), Chunk(1, "beta delay", Unit(0, 1, 0))], CancellationToken.None);

        // Concurrent reindexes while searching: every search must either see a consistent index or an empty one, never throw.
        var searches = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var inner = host.Scope();
            return await inner.ServiceProvider.GetRequiredService<IVectorStore>().SearchByKeywordAsync(1, "delay", 5, CancellationToken.None);
        }).ToList();
        var reindexes = Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var inner = host.Scope();
            await inner.ServiceProvider.GetRequiredService<IVectorStore>().ReplaceAllAsync(1, [Chunk(0, "alpha delay", Unit(1, 0, 0)), Chunk(1, "beta delay", Unit(0, 1, 0))], CancellationToken.None);
        }).ToList();

        var act = async () => await Task.WhenAll(searches.Cast<Task>().Concat(reindexes));
        await act.Should().NotThrowAsync();
    }
}
