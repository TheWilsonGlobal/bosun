using System.Text.Json.Nodes;
using Bosun.Agent;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Bosun.Infrastructure.Persistence;
using Bosun.Infrastructure.Seed;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Agent.Tests;

/// <summary>A scripted model: each call pops the next reply. Running out of script is a visible failure, never a silent success.</summary>
public sealed class ScriptedAgentLlm : ILlmClient
{
    private readonly Queue<Func<LlmRequest, CancellationToken, Task<LlmResponse>>> script = new();
    private int signatures;

    public List<LlmRequest> Requests { get; } = [];

    public string Provider => "scripted";

    public string DefaultModel => "scripted-model";

    public int DefaultMaxOutputTokens => 8192;

    public int MaxOutputTokensCap => 32768;

    public int Remaining => script.Count;

    public static LlmResponse Respond(IReadOnlyList<LlmPart> parts, int prompt = 100, int candidates = 20) => new()
    {
        Model = "scripted-model",
        Parts = parts,
        FinishReason = LlmFinishReason.Stop,
        FinishReasonRaw = "STOP",
        Usage = new LlmUsage(prompt, candidates, 0, prompt + candidates),
        Latency = TimeSpan.FromMilliseconds(5),
    };

    public ScriptedAgentLlm Then(Func<LlmRequest, CancellationToken, Task<LlmResponse>> reply)
    {
        script.Enqueue(reply);
        return this;
    }

    public ScriptedAgentLlm Then(Func<LlmRequest, LlmResponse> reply) => Then((r, _) => Task.FromResult(reply(r)));

    public ScriptedAgentLlm Plan(PlanDecision decision = PlanDecision.Proceed, string message = "", string title = "Test change", int tokens = 100, params string[] steps)
    {
        var plan = new JsonObject
        {
            ["decision"] = decision switch { PlanDecision.Proceed => "proceed", PlanDecision.Clarify => "clarify", _ => "refuse" },
            ["message"] = message,
            ["title"] = title,
            ["steps"] = new JsonArray((steps.Length == 0 ? ["Do the work"] : steps).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["affected_scope"] = "test scope",
            ["risks"] = new JsonArray(),
        };
        return Then(_ => Respond([new TextPart(plan.ToJsonString())], tokens, 20));
    }

    /// <summary>A model turn with one or more function calls; every call carries a thought signature like Gemini 3 does.</summary>
    public ScriptedAgentLlm Calls(params (string Name, JsonObject Args)[] calls) =>
        Then(_ => Respond(calls.Select(c => (LlmPart)new FunctionCallPart(c.Name, c.Args, null, $"sig-{++signatures}")).ToList()));

    public ScriptedAgentLlm Call(string name, JsonObject args) => Calls((name, args));

    public ScriptedAgentLlm Finish(string outcome, string message) =>
        Call(FinishTask.Name, new JsonObject { ["outcome"] = outcome, ["message"] = message });

    public ScriptedAgentLlm Text(string text) => Then(_ => Respond([new TextPart(text)]));

    public ScriptedAgentLlm Throw(LlmException ex) => Then((_, _) => throw ex);

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        if (!script.TryDequeue(out var reply))
        {
            throw new InvalidOperationException($"The scripted model has no reply left for call #{Requests.Count} ({request.Feature}).");
        }

        return await reply(request, cancellationToken);
    }
}

public sealed class RecordingSink : IAgentEventSink
{
    public List<AgentStepEvent> Steps { get; } = [];

    public List<AgentStatusEvent> Statuses { get; } = [];

    public List<AgentDoneEvent> Done { get; } = [];

    public Task StepAsync(AgentStepEvent e)
    {
        lock (Steps)
        {
            Steps.Add(e);
        }

        return Task.CompletedTask;
    }

    public Task StatusAsync(AgentStatusEvent e)
    {
        lock (Statuses)
        {
            Statuses.Add(e);
        }

        return Task.CompletedTask;
    }

    public Task DoneAsync(AgentDoneEvent e)
    {
        lock (Done)
        {
            Done.Add(e);
        }

        return Task.CompletedTask;
    }
}

/// <summary>The seeded database, built once per test run and copied per host: a real SQLite file, never EF InMemory.</summary>
public static class SeedFile
{
    private static readonly Lazy<string> Template = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Path => Template.Value;

    private static string Build()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bosun-agent-tests", "seed-" + Guid.NewGuid().ToString("N")[..8]);
        var file = System.IO.Path.Combine(dir, "bosun.seed.db");
        SeedDatabase.CreateAsync(file).GetAwaiter().GetResult();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(dir);
        return file;
    }

    public static void TryDelete(string dir)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort: the temp directory is outside the repo
        }
    }
}

/// <summary>The real agent stack (tools, runner, manager, EF on a copy of the seeded file) with a scripted model.</summary>
/// <summary>The agent tests never embed anything; search_docs needs the dependency to exist, and says 'not indexed' before it would be used.</summary>
public sealed class UnconfiguredEmbeddings : IEmbeddingClient
{
    public string Model => "none";

    public int Dimensions => 768;

    public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingTask task, string feature, CancellationToken cancellationToken) =>
        throw new LlmException(LlmErrorCodes.NotConfigured, "No embedding client in this test host.", attempts: 0);
}

public sealed class AgentHost : IDisposable
{
    private readonly ServiceProvider provider;
    private readonly string dir;

    public ScriptedAgentLlm Llm { get; }

    public RecordingSink Events { get; } = new();

    public string DbPath { get; }

    public AgentHost(ScriptedAgentLlm llm, Dictionary<string, string?>? config = null)
    {
        Llm = llm;
        dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bosun-agent-tests", "db-" + Guid.NewGuid().ToString("N")[..8]);
        DbPath = SeedDatabase.CopyTo(SeedFile.Path, dir);

        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "sqlite",
            ["Database:ConnectionString"] = $"Data Source={DbPath}",
        };
        foreach (var (key, value) in config ?? [])
        {
            settings[key] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddBosunDatabase();
        services.AddBosunApplication();
        services.AddBosunAgent();
        services.AddSingleton<ILlmClient>(llm);
        services.AddSingleton<IEmbeddingClient, UnconfiguredEmbeddings>();
        services.AddSingleton<StructuredLlm>();
        services.AddSingleton<IAgentEventSink>(Events);
        provider = services.BuildServiceProvider();
    }

    public AsyncServiceScope Scope() => provider.CreateAsyncScope();

    public T Get<T>()
        where T : notnull => provider.GetRequiredService<T>();

    public static UserContext Engineer { get; } = new("alice", new HashSet<string>());

    /// <summary>Starts a run through the manager (the real path), waits for it and returns the stored run with its steps.</summary>
    public async Task<AgentRunDto> RunAsync(string message, int projectId = 1)
    {
        var manager = Get<AgentRunManager>();
        var id = await manager.StartAsync(projectId, message, Engineer, CancellationToken.None);
        if (manager.Completion(id) is { } completion)
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(60));
        }

        return await GetRunAsync(id);
    }

    public async Task<AgentRunDto> GetRunAsync(int id)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<AgentQueries>().GetAsync(id, CancellationToken.None);
    }

    public async Task<ChangeSetDto> GetChangeSetAsync(int id)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<ChangeSetService>().GetAsync(id, CancellationToken.None);
    }

    /// <summary>A fingerprint of everything an apply could change: tags, their limits, groups and channels.</summary>
    public async Task<string> TagFingerprintAsync()
    {
        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        var tags = await db.Tags.AsNoTracking().OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.TagName, t.Description, t.Unit, t.RangeLow, t.RangeHigh, t.AlarmGroupId, t.ChannelId, t.Version, t.SafetyClass, t.SignalType })
            .ToListAsync();
        var limits = await db.AlarmLimits.AsNoTracking().OrderBy(l => l.Id).Select(l => new { l.Id, l.TagId, l.Level, l.Value, l.DelayS, l.Hysteresis }).ToListAsync();
        var groups = await db.AlarmGroups.AsNoTracking().OrderBy(g => g.Id).Select(g => new { g.Id, g.Name }).ToListAsync();
        return System.Text.Json.JsonSerializer.Serialize(new { tags, limits, groups }).GetHashCode().ToString() + "/" + tags.Count + "/" + limits.Count;
    }

    public async Task<List<string>> TagNamesAsync(string prefix, int take)
    {
        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        return await db.Tags.AsNoTracking().Where(t => t.TagName.StartsWith(prefix)).OrderBy(t => t.TagName).Select(t => t.TagName).Take(take).ToListAsync();
    }

    public async Task SetDescriptionAsync(string tagName, string description)
    {
        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        var tag = await db.Tags.FirstAsync(t => t.TagName == tagName);
        tag.Description = description;
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        provider.Dispose();
        SeedFile.TryDelete(dir);
    }
}

public static class Args
{
    public static JsonObject Obj(params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject();
        foreach (var (key, value) in fields)
        {
            o[key] = value;
        }

        return o;
    }

    public static JsonArray Names(IEnumerable<string> names) => new(names.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());

    public static JsonNode V(string s) => JsonValue.Create(s)!;

    public static JsonNode V(double d) => JsonValue.Create(d)!;

    /// <summary>propose_set_alarm_limit arguments.</summary>
    public static JsonObject SetLimit(IEnumerable<string> tags, string level, double value, double delay) =>
        Obj(("tag_names", Names(tags)), ("level", V(level)), ("value", V(value)), ("delay_s", V(delay)));

    public static JsonObject Assign(IEnumerable<string> tags, string group) => Obj(("tag_names", Names(tags)), ("alarm_group", V(group)));
}
