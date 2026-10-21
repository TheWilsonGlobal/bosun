using Bosun.Application.Monitoring;
using Bosun.Infrastructure.Configuration;
using Bosun.Protocols.Modbus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bosun.Infrastructure.Monitoring;

/// <summary>Runs the single alarm writer. Registered FIRST so that on shutdown it stops LAST, after the poller stopped producing.</summary>
public sealed class AlarmWriterService(AlarmEventWriter writer) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => writer.RunAsync(stoppingToken);
}

public static class MonitoringRegistration
{
    /// <summary>
    /// Live monitoring: register map, live values, alarm engine, the batch writer and (when <c>Monitoring:Enabled</c>) the Modbus poller
    /// and NMEA listener. With Enabled=false no socket is opened and the hosted services only report "disabled".
    /// </summary>
    public static IServiceCollection AddBosunMonitoring(this IServiceCollection services)
    {
        services.AddOptions<MonitoringOptions>().BindConfiguration(MonitoringOptions.Section);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MonitoringOptions>>().Value);
        services.TryAddSingleton<IMonitoringNotifier, NullMonitoringNotifier>();
        services.AddSingleton<RegisterMap>(sp =>
        {
            var options = sp.GetRequiredService<MonitoringOptions>();
            var root = sp.GetRequiredService<IConfiguration>()["Bosun:RepoRoot"] ?? RepoRoot.Resolve();
            var path = options.RegisterMapPath is { Length: > 0 } configured
                ? Path.GetFullPath(configured, root)
                : RegisterMap.FindDefaultFile(root, AppContext.BaseDirectory);
            if (path is not null && File.Exists(path))
            {
                return RegisterMap.Load(path);
            }

            // Monitoring off: nothing will poll, so a missing file is not an error (a deployment without the simulator folder).
            return options.Enabled
                ? throw new FileNotFoundException("The register map apps/simulator/mapping/registers.json was not found (set Monitoring:RegisterMapPath).")
                : RegisterMap.Parse("""{ "registers": [] }""");
        });
        services.AddSingleton<MonitoringState>();
        services.AddSingleton<IAlarmRepository, DbAlarmRepository>();
        services.AddSingleton<AlarmEventWriter>();
        services.AddSingleton<IAlarmWriteQueue>(sp => sp.GetRequiredService<AlarmEventWriter>());
        services.AddSingleton(sp => new AlarmManager(
            sp.GetRequiredService<MonitoringOptions>(),
            sp.GetRequiredService<RegisterMap>().Registers.Select(r => r.Tag).ToArray(),
            sp.GetRequiredService<IAlarmRepository>(),
            sp.GetRequiredService<IAlarmWriteQueue>(),
            sp.GetRequiredService<IMonitoringNotifier>(),
            sp.GetRequiredService<ConfigChangeNotifier>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<AlarmManager>>()));

        services.AddHostedService<AlarmWriterService>();
        services.AddSingleton<ModbusPollingService>();
        services.AddSingleton<NmeaListener>();
        services.AddHostedService(sp => sp.GetRequiredService<ModbusPollingService>());
        services.AddHostedService(sp => sp.GetRequiredService<NmeaListener>());
        return services;
    }
}
