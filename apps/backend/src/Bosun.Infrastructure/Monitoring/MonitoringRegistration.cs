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

/// <summary>SQLite codes that go away by themselves: BUSY (5), LOCKED (6), IOERR (10), FULL (13), CANTOPEN (14). Constraint and schema errors do not.</summary>
public sealed class SqliteWriteFailureClassifier : IWriteFailureClassifier
{
    private readonly DefaultWriteFailureClassifier fallback = new();

    public bool IsTransient(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is Microsoft.Data.Sqlite.SqliteException sqlite)
            {
                return sqlite.SqliteErrorCode is 5 or 6 or 10 or 13 or 14;
            }
        }

        return fallback.IsTransient(exception);
    }
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
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MonitoringOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.AlarmDeadLetterPath))
            {
                var root = sp.GetRequiredService<IConfiguration>()["Bosun:RepoRoot"] ?? RepoRoot.Resolve();
                options.AlarmDeadLetterPath = Path.Combine(root, "data", "logs", "alarm-deadletter.jsonl");
            }

            return options;
        });
        services.TryAddSingleton<IWriteFailureClassifier, SqliteWriteFailureClassifier>();
        services.TryAddSingleton<IMonitoringNotifier, NullMonitoringNotifier>();
        services.AddSingleton<RegisterMap>(sp =>
        {
            var options = sp.GetRequiredService<MonitoringOptions>();
            var root = sp.GetRequiredService<IConfiguration>()["Bosun:RepoRoot"] ?? RepoRoot.Resolve();
            var path = options.RegisterMapPath is { Length: > 0 } configured
                ? Path.GetFullPath(configured, root)
                : RegisterMap.FindDefaultFile(root, AppContext.BaseDirectory)
                  ?? (File.Exists(Path.Combine(root, "mapping", RegisterMap.FileName)) ? Path.Combine(root, "mapping", RegisterMap.FileName) : null)
                  ?? (File.Exists(Path.Combine(AppContext.BaseDirectory, "mapping", RegisterMap.FileName)) ? Path.Combine(AppContext.BaseDirectory, "mapping", RegisterMap.FileName) : null);
            if (path is not null && File.Exists(path))
            {
                return RegisterMap.Load(path);
            }

            // Monitoring off or register file missing: return empty registers rather than crashing the entire host
            return RegisterMap.Parse("""{ "registers": [] }""");
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
