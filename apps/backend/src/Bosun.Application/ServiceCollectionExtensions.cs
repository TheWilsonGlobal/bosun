using Bosun.Application.Rag;
using Bosun.Application.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBosunApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<WriteGate>();
        services.AddSingleton<Monitoring.ConfigChangeNotifier>();
        services.AddScoped<TagQueries>();
        services.AddScoped<ProjectQueries>();
        services.AddScoped<ChangeSetService>();
        services.AddScoped<ProjectFileService>();
        services.AddOptions<RagOptions>();
        services.AddSingleton<EmbeddingCache>();
        services.AddSingleton<KnowledgeIndexGate>();
        services.AddScoped<KnowledgeSearchService>();
        services.AddScoped<KnowledgeIngestService>();
        services.AddBosunTools();
        return services;
    }
}
