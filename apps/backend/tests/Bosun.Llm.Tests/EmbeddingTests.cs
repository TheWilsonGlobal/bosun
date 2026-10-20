using System.Net;
using System.Text.Json.Nodes;
using Bosun.Application.Llm;

namespace Bosun.Llm.Tests;

public class EmbeddingClientTests
{
    private static string Embeddings(params float[][] vectors) =>
        new JsonObject
        {
            ["embeddings"] = new JsonArray(vectors.Select(v => (JsonNode)new JsonObject { ["values"] = new JsonArray(v.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) }).ToArray()),
            ["usageMetadata"] = new JsonObject { ["promptTokenCount"] = 7 },
        }.ToJsonString();

    [Fact]
    public async Task Vectors_are_requested_at_the_configured_dimensions_and_come_back_l2_normalised()
    {
        using var host = new LlmHost(config: new() { ["Embedding:Dimensions"] = "4" });
        host.EmbeddingHandler.Then(HttpStatusCode.OK, Embeddings([3f, 4f, 0f, 0f], [1f, 1f, 1f, 1f]));

        var result = await host.Embeddings.EmbedAsync(["exhaust gas temperature", "lube oil pressure"], EmbeddingTask.Query, "rag.query", CancellationToken.None);

        result.Dimensions.Should().Be(4);
        result.Model.Should().Be("test-embedding");
        result.PromptTokens.Should().Be(7);
        result.Vectors.Should().HaveCount(2);
        result.Vectors[0].Should().Equal(0.6f, 0.8f, 0f, 0f);
        result.Vectors.Should().OnlyContain(v => Math.Abs(VectorMath.Norm(v) - 1) < 1e-6);

        var request = host.EmbeddingHandler.Requests.Single();
        request.Url.AbsoluteUri.Should().EndWith("/models/test-embedding:batchEmbedContents");
        request.KeyHeader.Should().Be(LlmHost.TestKey);
        var items = request.Json["requests"]!.AsArray();
        items.Should().HaveCount(2);
        items[0]!["outputDimensionality"]!.GetValue<int>().Should().Be(4);
        items[0]!["model"]!.GetValue<string>().Should().Be("models/test-embedding");
        items[0]!["taskType"]!.GetValue<string>().Should().Be("RETRIEVAL_QUERY");
        items[0]!["content"]!["parts"]![0]!["text"]!.GetValue<string>().Should().Be("exhaust gas temperature");
        host.Handler.Calls.Should().Be(0, "embeddings use their own client and key");
    }

    [Fact]
    public async Task Large_inputs_are_split_into_batches_and_keep_their_order()
    {
        using var host = new LlmHost(config: new() { ["Embedding:BatchSize"] = "2" });
        host.EmbeddingHandler
            .Then(HttpStatusCode.OK, Embeddings([1f, 0f, 0f, 0f], [0f, 1f, 0f, 0f]))
            .Then(HttpStatusCode.OK, Embeddings([0f, 0f, 1f, 0f]));

        var result = await host.Embeddings.EmbedAsync(["a", "b", "c"], EmbeddingTask.Document, "rag.index", CancellationToken.None);

        host.EmbeddingHandler.Requests.Select(r => r.Json["requests"]!.AsArray().Count).Should().Equal(2, 1);
        host.EmbeddingHandler.Requests[0].Json["requests"]![0]!["taskType"]!.GetValue<string>().Should().Be("RETRIEVAL_DOCUMENT");
        result.Vectors.Select(v => Array.IndexOf(v, 1f)).Should().Equal(0, 1, 2);
        result.PromptTokens.Should().Be(14);
    }

    [Fact]
    public async Task No_texts_means_no_call()
    {
        using var host = new LlmHost();
        var result = await host.Embeddings.EmbedAsync([], EmbeddingTask.Document, "rag.index", CancellationToken.None);
        result.Vectors.Should().BeEmpty();
        host.EmbeddingHandler.Calls.Should().Be(0);
        host.Log.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wrong_dimension_or_count_is_llm_malformed_output()
    {
        using var wrongDims = new LlmHost();
        wrongDims.EmbeddingHandler.Then(HttpStatusCode.OK, Embeddings([1f, 0f, 0f]));
        var act = async () => await wrongDims.Embeddings.EmbedAsync(["a"], EmbeddingTask.Document, "t", CancellationToken.None);
        (await act.Should().ThrowAsync<LlmException>()).Which.Message.Should().Contain("3 dimensions").And.Contain("EMBEDDING_DIMS");

        using var wrongCount = new LlmHost();
        wrongCount.EmbeddingHandler.Then(HttpStatusCode.OK, Embeddings([1f, 0f, 0f, 0f]));
        var act2 = async () => await wrongCount.Embeddings.EmbedAsync(["a", "b"], EmbeddingTask.Document, "t", CancellationToken.None);
        (await act2.Should().ThrowAsync<LlmException>()).Which.Code.Should().Be(LlmErrorCodes.MalformedOutput);
    }

    [Fact]
    public async Task The_embedding_key_is_separate_and_errors_name_its_variable()
    {
        using var host = new LlmHost(config: new() { ["Embedding:ApiKey"] = "" });
        var act = async () => await host.Embeddings.EmbedAsync(["a"], EmbeddingTask.Document, "t", CancellationToken.None);
        var failure = (await act.Should().ThrowAsync<LlmException>()).Which;
        failure.Code.Should().Be(LlmErrorCodes.NotConfigured);
        failure.Message.Should().Contain("GEMINI_EMBEDDING_API_KEY");
        host.Log.Calls.Should().BeEmpty("a call that never left is not a call");

        using var rejected = new LlmHost();
        rejected.EmbeddingHandler.Then(HttpStatusCode.BadRequest, FakeHandler.Error(400, "INVALID_ARGUMENT", "API key not valid. Please pass a valid API key.", ("API_KEY_INVALID", null)));
        var act2 = async () => await rejected.Embeddings.EmbedAsync(["a"], EmbeddingTask.Document, "t", CancellationToken.None);
        (await act2.Should().ThrowAsync<LlmException>()).Which.Message.Should().Contain("GEMINI_LLM_API_KEY", "the shared message names both variables");
    }

    [Fact]
    public async Task Embedding_429s_are_retried_like_text_calls()
    {
        using var host = new LlmHost();
        host.EmbeddingHandler
            .Then(HttpStatusCode.TooManyRequests, FakeHandler.Error(429, "RESOURCE_EXHAUSTED", "quota", ("", "0.01s")))
            .Then(HttpStatusCode.OK, Embeddings([1f, 0f, 0f, 0f]));

        var result = await host.Embeddings.EmbedAsync(["a"], EmbeddingTask.Document, "t", CancellationToken.None);

        result.Attempts.Should().Be(2);
    }
}
