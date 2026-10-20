using Bosun.Application.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBosunApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<WriteGate>();
        services.AddScoped<TagQueries>();
        services.AddScoped<ProjectQueries>();
        services.AddScoped<ChangeSetService>();
        services.AddScoped<ProjectFileService>();
        services.AddBosunTools();
        return services;
    }
}
