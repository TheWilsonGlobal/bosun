using System.Net;
using Bosun.Agent;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Domain.Entities;
using Bosun.Infrastructure.Configuration;
using Bosun.Infrastructure.Llm;
using Bosun.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bosun.Llm.Tests;

/// <summary>llm_calls on a real SQLite file (migrated by the real migrations), the price table, and the usage summary.</summary>
public sealed class LlmCallPersistenceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "bosun-llm-tests", Guid.NewGuid().ToString("N")[..8]);
    private readonly ServiceProvider provider;

    public LlmCallPersistenceTests()
    {
        Directory.CreateDirectory(dir);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "sqlite",
            ["Database:ConnectionString"] = $"Data Source={Path.Combine(dir, "bosun.db")}",
            ["Llm:Provider"] = "gemini",
            ["Llm:Model"] = "test-model",
            ["Llm:ApiKey"] = LlmHost.TestKey,
            ["Llm:Resilience:BaseDelayMs"] = "1",
            ["Llm:Pricing:Models:test-model:InputPerMillionUsd"] = "0.10",
            ["Llm:Pricing:Models:test-model:OutputPerMillionUsd"] = "0.40",
            ["Embedding:Model"] = "test-embedding",
            ["Embedding:ApiKey"] = LlmHost.TestKey,
            ["Embedding:Dimensions"] = "4",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddBosunDatabase();
        services.AddBosunLlm();
        Handler = new FakeHandler();
        EmbeddingHandler = new FakeHandler();
        services.AddHttpClient(GeminiHttp.LlmClientName).ConfigurePrimaryHttpMessageHandler(() => Handler);
        services.AddHttpClient(GeminiHttp.EmbeddingClientName).ConfigurePrimaryHttpMessageHandler(() => EmbeddingHandler);
        provider = services.BuildServiceProvider();
        DatabaseInitializer.MigrateAsync(provider).GetAwaiter().GetResult();
    }

    private FakeHandler Handler { get; }

    private FakeHandler EmbeddingHandler { get; }

    private async Task<List<LlmCall>> RowsAsync()
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BosunDbContext>().LlmCalls.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
    }

    [Fact]
    public async Task A_successful_call_persists_tokens_latency_prompt_version_and_an_estimated_cost()
    {
        Handler.ThenOk("pong", prompt: 1_000_000, candidates: 500_000, thoughts: 500_000);
        var before = DateTime.UtcNow;

        await provider.GetRequiredService<ILlmClient>().GenerateAsync(
            new LlmRequest { Feature = "ping", PromptVersion = "ping.system@deadbeef", Messages = [LlmMessage.User("hi")], AgentRunId = 7 },
            CancellationToken.None);

        var row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.Should().Match<LlmCall>(c =>
            c.Kind == "llm" && c.Feature == "ping" && c.Provider == "gemini" && c.Model == "test-model" && c.PromptVersion == "ping.system@deadbeef"
            && c.PromptTokens == 1_000_000 && c.CandidateTokens == 500_000 && c.ThoughtTokens == 500_000 && c.TotalTokens == 2_000_000
            && c.Attempts == 1 && c.Succeeded && c.ErrorCode == null && c.FinishReason == "STOP" && c.AgentRunId == 7);
        row.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        row.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        row.LatencyMs.Should().BeGreaterThanOrEqualTo(0);

        // rows carry the requested model, priced from the table: 1M in x 0.10 + 1M out x 0.40
        row.CostUsd.Should().BeApproximately(0.50, 1e-9);
    }

    [Fact]
    public async Task The_price_table_applies_per_model_and_bills_reasoning_at_the_output_rate()
    {
        var pricing = provider.GetRequiredService<LlmPricing>();
        pricing.Estimate("test-model", new LlmUsage(1_000_000, 500_000, 500_000, 2_000_000)).Should().BeApproximately(0.10 + 0.40, 1e-9);
        pricing.Estimate("test-model-001", new LlmUsage(1_000_000, 0, 0, 1_000_000)).Should().BeApproximately(0.10, 1e-9, "a provider suffix still finds the configured price by longest prefix");
        pricing.Estimate("unpriced-model", new LlmUsage(1_000_000, 1_000_000, 0, 2_000_000)).Should().Be(0);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_failed_call_is_recorded_with_its_error_code_and_attempts()
    {
        Handler.Then(HttpStatusCode.BadRequest, FakeHandler.Error(400, "INVALID_ARGUMENT", "API key not valid.", ("API_KEY_INVALID", null)));

        var act = async () => await provider.GetRequiredService<ILlmClient>().GenerateAsync(LlmHost.Hello("agent.plan"), CancellationToken.None);
        await act.Should().ThrowAsync<LlmException>();

        var row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.Should().Match<LlmCall>(c => !c.Succeeded && c.ErrorCode == "llm_auth_failed" && c.Feature == "agent.plan" && c.PromptTokens == 0 && c.Attempts == 1);
    }

    [Fact]
    public async Task Embedding_calls_are_recorded_as_embedding_rows()
    {
        EmbeddingHandler.Then(HttpStatusCode.OK, """{"embeddings":[{"values":[3,4,0,0]},{"values":[0,0,5,12]}],"usageMetadata":{"promptTokenCount":9}}""");

        var result = await provider.GetRequiredService<IEmbeddingClient>().EmbedAsync(["a", "b"], EmbeddingTask.Document, "rag.index", CancellationToken.None);

        result.Vectors.Should().HaveCount(2);
        var row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.Should().Match<LlmCall>(c => c.Kind == "embedding" && c.Feature == "rag.index" && c.Model == "test-embedding" && c.PromptTokens == 9 && c.Succeeded);
    }

    [Fact]
    public async Task The_usage_summary_groups_by_feature_and_model_and_the_report_prints_a_total()
    {
        Handler.ThenOk("a", prompt: 100, candidates: 10, thoughts: 5).ThenOk("b", prompt: 200, candidates: 20, thoughts: 0).ThenOk("c", prompt: 50, candidates: 5, thoughts: 0)
            .Then(HttpStatusCode.BadRequest, FakeHandler.Error(400, "INVALID_ARGUMENT", "nope"));
        var llm = provider.GetRequiredService<ILlmClient>();
        await llm.GenerateAsync(LlmHost.Hello("alpha"), CancellationToken.None);
        await llm.GenerateAsync(LlmHost.Hello("alpha"), CancellationToken.None);
        await llm.GenerateAsync(LlmHost.Hello("beta"), CancellationToken.None);
        await FluentActions.Awaiting(() => llm.GenerateAsync(LlmHost.Hello("beta"), CancellationToken.None)).Should().ThrowAsync<LlmException>();

        await using var scope = provider.CreateAsyncScope();
        var usage = scope.ServiceProvider.GetRequiredService<LlmUsageQueries>();
        var summary = await usage.SummariseAsync(null, null, CancellationToken.None);

        summary.Rows.Should().HaveCount(2);
        var alpha = summary.Rows.Single(r => r.Feature == "alpha");
        alpha.Should().Match<LlmUsageRow>(r => r.Calls == 2 && r.Failures == 0 && r.PromptTokens == 300 && r.CandidateTokens == 30 && r.ThoughtTokens == 5 && r.Kind == "llm");
        var beta = summary.Rows.Single(r => r.Feature == "beta");
        beta.Should().Match<LlmUsageRow>(r => r.Calls == 2 && r.Failures == 1 && r.PromptTokens == 50);
        summary.Totals.Should().Match<LlmUsageRow>(r => r.Calls == 4 && r.Failures == 1 && r.PromptTokens == 350);

        var text = LlmUsageReport.Format(summary.Rows);
        text.Should().Contain("alpha").And.Contain("beta").And.Contain("TOTAL").And.Contain("ESTIMATES");

        var future = await usage.SummariseAsync(DateTime.UtcNow.AddHours(1), null, CancellationToken.None);
        future.Rows.Should().BeEmpty();
        future.Totals.Calls.Should().Be(0);
        var past = await usage.SummariseAsync(null, DateTime.UtcNow.AddHours(-1), CancellationToken.None);
        past.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Each_call_logs_one_structured_line_with_the_fields_the_plan_asks_for()
    {
        var logger = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logger));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Model"] = "test-model",
            ["Llm:ApiKey"] = LlmHost.TestKey,
        }).Build());
        services.AddSingleton(TimeProvider.System);
        services.AddBosunLlm();
        var handler = new FakeHandler().ThenOk("x", prompt: 11, candidates: 22, thoughts: 33);
        services.AddHttpClient(GeminiHttp.LlmClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<ILlmCallLog>(new MemoryCallLog());
        await using var sp = services.BuildServiceProvider();

        await sp.GetRequiredService<ILlmClient>().GenerateAsync(LlmHost.Hello("ping") with { PromptVersion = "ping.system@abc" }, CancellationToken.None);

        var line = logger.Entries.Single(e => e.Message.StartsWith("llm_call", StringComparison.Ordinal));
        line.Properties["feature"].Should().Be("ping");
        line.Properties["model"].Should().Be("test-model");
        line.Properties["prompt_version"].Should().Be("ping.system@abc");
        line.Properties["tokens_in"].Should().Be(11);
        line.Properties["tokens_out"].Should().Be(22);
        line.Properties["tokens_thought"].Should().Be(33);
        line.Properties.Should().ContainKey("latency_ms");
        logger.Entries.Should().NotContain(e => e.Message.Contains(LlmHost.TestKey), "the key never reaches a log line");
    }

    public void Dispose()
    {
        provider.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // temp dir, best effort
        }
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public sealed record Entry(string Message, Dictionary<string, object?> Properties);

    private readonly List<Entry> entries = [];

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (entries)
            {
                return entries.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    properties[key] = value;
                }
            }

            lock (owner.entries)
            {
                owner.entries.Add(new Entry(formatter(state, exception), properties));
            }
        }
    }
}

public class PromptLibraryTests
{
    [Fact]
    public void The_library_loads_the_prompt_files_embedded_from_Bosun_Agent_prompts()
    {
        var library = new PromptLibrary();

        var ping = library.Get("ping.system");

        ping.Text.Should().Contain("health probe").And.Contain("<data>");
        ping.Name.Should().Be("ping.system");
        ping.Hash.Should().MatchRegex("^[0-9a-f]{8}$");
        ping.Version.Should().Be($"ping.system@{ping.Hash}");
        library.All.Should().Contain(p => p.Name == "ping.system");
    }

    [Fact]
    public void The_hash_is_the_sha256_prefix_of_the_normalised_text_and_changes_with_any_edit()
    {
        // known vector: sha256("hello") = 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824
        Prompt.HashOf("hello").Should().Be("2cf24dba");
        Prompt.HashOf("hello\r\n").Should().Be("2cf24dba", "CRLF checkouts and trailing newlines do not change the version");
        Prompt.HashOf("hello!").Should().NotBe("2cf24dba");
        Prompt.HashOf("hello world").Should().NotBe(Prompt.HashOf("hello  world"), "a whitespace edit inside the text is a new version");
    }

    [Fact]
    public void Placeholders_render_and_a_missing_value_is_an_error()
    {
        var prompt = new Prompt("t", "Hello {{name}}, project {{project.id}}.", Prompt.HashOf("x"));

        prompt.Render(new Dictionary<string, string> { ["name"] = "Ann", ["project.id"] = "7" }).Should().Be("Hello Ann, project 7.");
        var act = () => prompt.Render(new Dictionary<string, string> { ["name"] = "Ann" });
        act.Should().Throw<KeyNotFoundException>().WithMessage("*project.id*");
    }

    [Fact]
    public void An_unknown_prompt_lists_the_known_ones()
    {
        var act = () => new PromptLibrary().Get("nope");
        act.Should().Throw<KeyNotFoundException>().WithMessage("*nope*ping.system*");
    }

    [Fact]
    public void Several_prompts_combine_into_one_stable_version()
    {
        var a = new Prompt("a", "A", Prompt.HashOf("A"));
        var b = new Prompt("b", "B", Prompt.HashOf("B"));

        Prompt.CombinedVersion(a).Should().Be(a.Version);
        var combined = Prompt.CombinedVersion(a, b);
        combined.Should().MatchRegex("^a\\+b@[0-9a-f]{8}$");
        Prompt.CombinedVersion(a, b).Should().Be(combined);
        Prompt.CombinedVersion(b, a).Should().NotBe(combined);
    }
}

public class EnvironmentMappingTests
{
    [Fact]
    public void The_env_file_variables_map_onto_the_llm_and_embedding_configuration_and_blank_means_unset()
    {
        var root = Path.Combine(Path.GetTempPath(), "bosun-root-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Bosun.sln"), "");
            File.WriteAllText(
                Path.Combine(root, ".env"),
                "LLM_PROVIDER=gemini\nLLM_MODEL=some-model\nGEMINI_LLM_API_KEY=llm-key-value\nEMBEDDING_MODEL=emb-model\nEMBEDDING_DIMS=256\nGEMINI_EMBEDDING_API_KEY=\n# GEMINI_EMBEDDING_API_KEY=commented-out\n");

            var configuration = new ConfigurationBuilder().AddBosunEnv(root).Build();

            configuration["Llm:Provider"].Should().Be("gemini");
            configuration["Llm:Model"].Should().Be("some-model");
            configuration["Llm:ApiKey"].Should().Be("llm-key-value");
            configuration["Embedding:Model"].Should().Be("emb-model");
            configuration["Embedding:Dimensions"].Should().Be("256");
            configuration["Embedding:ApiKey"].Should().BeNull("an empty value is unset, and a commented-out line is ignored");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Key_options_never_print_their_key()
    {
        var options = new LlmOptions { ApiKey = "super-secret-key-value" };
        options.ToString().Should().NotContain("super-secret");
    }
}

public class UsageTallyTests
{
    [Fact]
    public void The_tally_prints_the_same_table_for_runs_without_a_database()
    {
        var tally = new LlmUsageTally();
        tally.Add("agent.plan", new LlmResponse { Model = "m", Parts = [], FinishReason = LlmFinishReason.Stop, Usage = new LlmUsage(100, 20, 30, 150), Latency = TimeSpan.FromMilliseconds(400), EstimatedCostUsd = 0.001 });
        tally.Add("agent.plan", new LlmResponse { Model = "m", Parts = [], FinishReason = LlmFinishReason.Stop, Usage = new LlmUsage(50, 10, 0, 60), Latency = TimeSpan.FromMilliseconds(200), EstimatedCostUsd = 0.0005 });

        var text = tally.Format();

        tally.Rows().Single().Should().Match<LlmUsageRow>(r => r.Calls == 2 && r.PromptTokens == 150 && r.ThoughtTokens == 30 && r.MaxLatencyMs == 400);
        text.Should().Contain("agent.plan").And.Contain("TOTAL").And.Contain("0.001500");
    }
}
