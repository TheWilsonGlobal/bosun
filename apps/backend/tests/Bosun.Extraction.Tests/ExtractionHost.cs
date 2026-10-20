using Bosun.Agent.Tests;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Domain;
using Bosun.Evals.Offline;
using Bosun.Extraction;
using Bosun.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Extraction.Tests;

/// <summary>The real extraction stack (service, runner, manager, EF on a copy of the seeded SQLite file, a real document store) with a scripted reader.</summary>
public sealed class ExtractionHost : IDisposable
{
    private readonly ServiceProvider provider;
    private readonly string dir;

    public ILlmClient Llm { get; }

    public string DbPath { get; }

    public string StoreDir { get; }

    public static UserContext Engineer { get; } = new("alice", new HashSet<string>());

    public ExtractionHost(ILlmClient? llm = null, Dictionary<string, string?>? config = null)
    {
        Llm = llm ?? new TableParsingLlm();
        dir = Path.Combine(Path.GetTempPath(), "bosun-extraction-tests", "db-" + Guid.NewGuid().ToString("N")[..8]);
        DbPath = Bosun.Infrastructure.Seed.SeedDatabase.CopyTo(SeedFile.Path, dir);
        StoreDir = Path.Combine(dir, "documents");

        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "sqlite",
            ["Database:ConnectionString"] = $"Data Source={DbPath}",
            ["Extraction:StoragePath"] = StoreDir,
            ["Extraction:DualReadSamplePages"] = "0",
            ["Extraction:DualReadLowScoreShare"] = "2",
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
        services.AddBosunExtraction();
        services.AddSingleton(Llm);
        services.AddSingleton<StructuredLlm>();
        provider = services.BuildServiceProvider();
    }

    public AsyncServiceScope Scope() => provider.CreateAsyncScope();

    public T Get<T>()
        where T : notnull => provider.GetRequiredService<T>();

    /// <summary>Uploads through the service and waits for the background run to finish.</summary>
    public async Task<(UploadResultDto Ids, ExtractionDto Extraction)> UploadAndRunAsync(byte[] pdf, string fileName = "list.pdf", UserContext? user = null)
    {
        UploadResultDto ids;
        await using (var scope = Scope())
        {
            using var stream = new MemoryStream(pdf);
            ids = await scope.ServiceProvider.GetRequiredService<ExtractionService>().UploadAsync(1, stream, fileName, user ?? Engineer, CancellationToken.None);
        }

        if (Get<ExtractionRunManager>().Completion(ids.ExtractionId) is { } completion)
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(120));
        }

        return (ids, await GetAsync(ids.ExtractionId));
    }

    public async Task<ExtractionDto> GetAsync(int extractionId)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<ExtractionService>().GetAsync(extractionId, CancellationToken.None);
    }

    public async Task<T> WithServiceAsync<T>(Func<ExtractionService, Task<T>> action)
    {
        await using var scope = Scope();
        return await action(scope.ServiceProvider.GetRequiredService<ExtractionService>());
    }

    public async Task<T> WithDbAsync<T>(Func<BosunDbContext, Task<T>> action)
    {
        await using var scope = Scope();
        return await action(scope.ServiceProvider.GetRequiredService<BosunDbContext>());
    }

    public Task<int> TagCountAsync() => WithDbAsync(db => db.Tags.CountAsync());

    public void Dispose()
    {
        provider.Dispose();
        SeedFile.TryDelete(dir);
    }
}
