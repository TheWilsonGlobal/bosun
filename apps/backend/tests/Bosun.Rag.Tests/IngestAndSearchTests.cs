using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Application.Rag;
using Bosun.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Rag.Tests;

public sealed class IngestTests : IDisposable
{
    private readonly RagHost host = new();

    public void Dispose() => host.Dispose();

    private async Task<IndexReport> Reindex(bool cacheOnly = false)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<KnowledgeIngestService>().ReindexAsync(1, cacheOnly, CancellationToken.None);
    }

    [Fact]
    public async Task The_first_reindex_embeds_every_chunk_in_batches_and_stores_citation_fields()
    {
        var report = await Reindex();

        report.Skipped.Should().BeFalse();
        report.Documents.Should().Be(3);
        report.Chunks.Should().Be(15);
        report.Embedded.Should().Be(15);
        report.FromStore.Should().Be(0);
        report.FromCache.Should().Be(0);
        host.Embeddings.Calls.Should().ContainSingle().Which.Should().Be((EmbeddingTask.Document, "rag.index", 15), "one batched request for all chunks");

        await using var scope = host.Scope();
        var status = await scope.ServiceProvider.GetRequiredService<KnowledgeIngestService>().StatusAsync(1, CancellationToken.None);
        status.Indexed.Should().BeTrue();
        status.Documents.Should().Be(3);
        status.Chunks.Should().Be(15);
        status.EmbeddedChunks.Should().Be(15);
        status.DocumentsOnDisk.Should().Be(3);
        status.EmbeddingModel.Should().Be("fake-embedding");
        status.Sources.Select(s => s.Source).Should().Equal("alarm-philosophy", "cause-and-effect", "functional-design-spec");

        var chunks = await scope.ServiceProvider.GetRequiredService<IBosunDbContext>().KnowledgeChunks.AsNoTracking().ToListAsync();
        chunks.Should().OnlyContain(c => c.Source != "" && c.Section != "" && c.Page >= 1 && c.ContentHash.Length == 64 && c.Embedding != null && c.Embedding.Length == FakeEmbeddingClient.Slots * sizeof(float));
    }

    [Fact]
    public async Task A_second_reindex_of_unchanged_documents_embeds_nothing_and_makes_no_request()
    {
        await Reindex();
        host.Embeddings.Calls.Clear();

        var report = await Reindex();

        report.Embedded.Should().Be(0);
        report.EmbeddingRequests.Should().Be(0);
        report.FromStore.Should().Be(15);
        host.Embeddings.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Changing_one_section_re_embeds_only_the_changed_chunk_and_a_removed_document_drops_its_chunks()
    {
        await Reindex();
        host.Embeddings.Calls.Clear();
        var file = Path.Combine(host.DocsDir, "functional-design-spec.md");
        File.WriteAllText(file, File.ReadAllText(file).Replace("at most 24 characters", "at most 30 characters", StringComparison.Ordinal));

        var report = await Reindex();

        report.Embedded.Should().Be(1);
        report.FromStore.Should().Be(14);
        host.Embeddings.Calls.Should().ContainSingle().Which.Texts.Should().Be(1);

        File.Delete(Path.Combine(host.DocsDir, "cause-and-effect.md"));
        host.Embeddings.Calls.Clear();
        var afterDelete = await Reindex();
        afterDelete.Embedded.Should().Be(0);
        afterDelete.Chunks.Should().Be(11);
        await using var scope = host.Scope();
        (await scope.ServiceProvider.GetRequiredService<IBosunDbContext>().KnowledgeChunks.CountAsync()).Should().Be(11);
    }

    [Fact]
    public async Task Reseeding_a_fresh_database_with_the_same_documents_is_served_from_the_cache_file()
    {
        await Reindex();
        var cacheBytes = File.ReadAllBytes(host.CachePath);

        // db:reset deletes the database; the embedding cache file stays.
        using var reseeded = new RagHost();
        File.WriteAllBytes(reseeded.CachePath, cacheBytes);
        await using var scope = reseeded.Scope();
        var report = await scope.ServiceProvider.GetRequiredService<KnowledgeIngestService>().ReindexAsync(1, cacheOnly: false, CancellationToken.None);

        report.FromCache.Should().Be(15);
        report.Embedded.Should().Be(0);
        reseeded.Embeddings.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cache_only_mode_indexes_when_everything_is_cached_and_otherwise_changes_nothing_and_calls_nothing()
    {
        var empty = await Reindex(cacheOnly: true);

        empty.Skipped.Should().BeTrue();
        host.Embeddings.Calls.Should().BeEmpty();
        await using (var scope = host.Scope())
        {
            (await scope.ServiceProvider.GetRequiredService<KnowledgeIngestService>().StatusAsync(1, CancellationToken.None)).Indexed.Should().BeFalse();
        }

        await Reindex();
        using var fresh = new RagHost();
        File.Copy(host.CachePath, fresh.CachePath, overwrite: true);
        await using var freshScope = fresh.Scope();
        var report = await freshScope.ServiceProvider.GetRequiredService<KnowledgeIngestService>().ReindexAsync(1, cacheOnly: true, CancellationToken.None);

        report.Skipped.Should().BeFalse();
        report.FromCache.Should().Be(15);
        fresh.Embeddings.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_failure_changes_nothing_and_a_concurrent_reindex_is_refused()
    {
        host.Embeddings.Fail = new LlmException(LlmErrorCodes.RateLimited, "quota", 429, attempts: 1);
        var act = async () => await Reindex();
        (await act.Should().ThrowAsync<LlmException>()).Which.Code.Should().Be(LlmErrorCodes.RateLimited);
        await using (var scope = host.Scope())
        {
            (await scope.ServiceProvider.GetRequiredService<IBosunDbContext>().KnowledgeChunks.CountAsync()).Should().Be(0, "all or nothing");
        }

        host.Embeddings.Fail = null;
        var gate = host.Get<KnowledgeIndexGate>();
        gate.TryEnter().Should().BeTrue();
        try
        {
            var busy = async () => await Reindex();
            (await busy.Should().ThrowAsync<AppException>()).Which.Code.Should().Be(KnowledgeErrorCodes.ReindexInProgress);
        }
        finally
        {
            gate.Exit();
        }
    }

    [Fact]
    public async Task Unknown_project_and_empty_docs_folder_are_reported()
    {
        await using var scope = host.Scope();
        var ingest = scope.ServiceProvider.GetRequiredService<KnowledgeIngestService>();

        var missing = async () => await ingest.ReindexAsync(99, false, CancellationToken.None);
        (await missing.Should().ThrowAsync<AppException>()).Which.Code.Should().Be("not_found");

        foreach (var file in Directory.EnumerateFiles(host.DocsDir))
        {
            File.Delete(file);
        }

        var none = async () => await ingest.ReindexAsync(1, false, CancellationToken.None);
        (await none.Should().ThrowAsync<AppException>()).Which.Code.Should().Be(KnowledgeErrorCodes.NoDocuments);
    }
}

public sealed class SearchTests : IDisposable
{
    private readonly RagHost host = new();

    public void Dispose() => host.Dispose();

    private async Task IndexAsync()
    {
        await using var scope = host.Scope();
        await scope.ServiceProvider.GetRequiredService<KnowledgeIngestService>().ReindexAsync(1, false, CancellationToken.None);
    }

    private async Task<RetrievalResult> SearchAsync(string question, int? k = null)
    {
        await using var scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<KnowledgeSearchService>().SearchAsync(1, question, k, CancellationToken.None);
    }

    [Fact]
    public async Task Hybrid_search_finds_the_right_section_with_scores_from_both_retrievers()
    {
        await IndexAsync();

        var result = await SearchAsync("why is the exhaust gas temperature thermocouple lag a reason for the delay");

        result.BelowThreshold.Should().BeFalse();
        result.Chunks.Should().HaveCountLessThanOrEqualTo(6);
        result.Chunks[0].Section.Should().Be("Exhaust gas temperature delay");
        result.Chunks[0].Cosine.Should().BeGreaterThan(result.MinCosine);
        result.Chunks[0].Bm25.Should().BeGreaterThan(0);
        result.Chunks[0].VectorRank.Should().NotBeNull();
        result.Chunks[0].KeywordRank.Should().NotBeNull();
        result.Chunks.Select(c => c.Score).Should().BeInDescendingOrder();
        result.Relevant.Should().OnlyContain(c => c.AboveThreshold);
    }

    [Fact]
    public async Task Top_k_is_respected_and_the_query_embedding_is_requested_as_a_query()
    {
        await IndexAsync();
        host.Embeddings.Calls.Clear();

        var result = await SearchAsync("alarm delay and hysteresis for temperature", k: 2);

        result.Chunks.Should().HaveCount(2);
        host.Embeddings.Calls.Should().ContainSingle().Which.Should().Be((EmbeddingTask.Query, "rag.query", 1));
    }

    [Fact]
    public async Task A_repeated_question_is_served_from_the_embedding_cache()
    {
        await IndexAsync();
        host.Embeddings.Calls.Clear();

        await SearchAsync("what is the shelving limit?");
        await SearchAsync("what is the shelving limit?");

        host.Embeddings.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Nothing_above_the_threshold_means_below_threshold_even_though_rrf_always_has_a_best_chunk()
    {
        await IndexAsync();

        var result = await SearchAsync("zxqv quokka marmalade spaceship");

        result.BelowThreshold.Should().BeTrue();
        result.Relevant.Should().BeEmpty();
        result.TopCosine.Should().BeLessThan(result.MinCosine);
    }

    [Fact]
    public async Task A_stricter_threshold_turns_the_same_question_into_a_refusal()
    {
        await IndexAsync();
        using var strict = new RagHost(minCosine: 0.99);
        await using (var scope = strict.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<KnowledgeIngestService>().ReindexAsync(1, false, CancellationToken.None);
        }

        await using var strictScope = strict.Scope();
        var result = await strictScope.ServiceProvider.GetRequiredService<KnowledgeSearchService>().SearchAsync(1, "Why is the exhaust gas temperature delay 10 s?", null, CancellationToken.None);

        result.Chunks.Should().NotBeEmpty("the ranking exists");
        result.BelowThreshold.Should().BeTrue();
    }

    [Fact]
    public async Task Search_errors_are_specific_not_indexed_invalid_question_unknown_project()
    {
        var notIndexed = async () => await SearchAsync("anything");
        (await notIndexed.Should().ThrowAsync<AppException>()).Which.Code.Should().Be(KnowledgeErrorCodes.NotIndexed);

        await IndexAsync();
        foreach (var bad in new[] { "", "   ", new string('x', 501), "bell\a" })
        {
            var act = async () => await SearchAsync(bad);
            (await act.Should().ThrowAsync<AppException>()).Which.Code.Should().Be(KnowledgeErrorCodes.InvalidQuestion);
        }

        await using var scope = host.Scope();
        var missing = async () => await scope.ServiceProvider.GetRequiredService<KnowledgeSearchService>().SearchAsync(99, "x y z", null, CancellationToken.None);
        (await missing.Should().ThrowAsync<AppException>()).Which.Code.Should().Be("not_found");
    }
}
