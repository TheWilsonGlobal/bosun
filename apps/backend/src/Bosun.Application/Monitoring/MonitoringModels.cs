using System.Text.Json.Serialization;
using Bosun.Domain;
using Bosun.Domain.Entities;

namespace Bosun.Application.Monitoring;

/// <summary>Settings under the <c>Monitoring</c> configuration section. Ports come from SIM_MODBUS_PORT / SIM_NMEA_PORT in the root .env.</summary>
public sealed class MonitoringOptions
{
    public const string Section = "Monitoring";

    /// <summary>Master switch. Integration tests turn it off so nothing opens a socket unless a test starts its own simulator.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The configurator project whose alarm limits apply to the live tags.</summary>
    public int ProjectId { get; set; } = 1;

    /// <summary>Register-to-tag file. Default: <c>apps/simulator/mapping/registers.json</c> found from the repo root.</summary>
    public string? RegisterMapPath { get; set; }

    public ModbusOptions Modbus { get; set; } = new();

    public NmeaOptions Nmea { get; set; } = new();

    /// <summary>The batched <c>values</c> event goes out at most this often, so a tag is pushed at most 2 times a second.</summary>
    public int BroadcastIntervalMs { get; set; } = 500;

    /// <summary>How long the alarm writer waits to gather a burst of alarm events into one transaction.</summary>
    public int AlarmFlushIntervalMs { get; set; } = 200;

    public int AlarmBatchSize { get; set; } = 200;

    /// <summary>Alarm events the write queue holds before producers have to wait (a healthy writer never gets near this).</summary>
    public int AlarmQueueCapacity { get; set; } = 10_000;

    /// <summary>How long a producer waits for room in a full queue before the event is given up (and dead-lettered, never silently).</summary>
    public int AlarmEnqueueBlockMs { get; set; } = 2000;

    /// <summary>Delay before the first retry of a transient write failure; it doubles up to <see cref="AlarmRetryMaxMs"/>.</summary>
    public int AlarmRetryInitialMs { get; set; } = 250;

    public int AlarmRetryMaxMs { get; set; } = 5000;

    /// <summary>On shutdown the writer keeps trying to store what is queued for this long.</summary>
    public int AlarmShutdownFlushSeconds { get; set; } = 5;

    /// <summary>JSON-lines file for alarm events that could not be stored. Default: <c>data/logs/alarm-deadletter.jsonl</c> under the repo root.</summary>
    public string? AlarmDeadLetterPath { get; set; }

    /// <summary>Limits are re-read at least this often even without an apply notification (changes made outside the API).</summary>
    public int LimitsMaxAgeSeconds { get; set; } = 60;

    /// <summary>
    /// A pending alarm delay is only counted over a run of samples without a hole: if a tag gets no sample for longer than this (the
    /// link dropped, the poller stalled), the delay restarts with the next sample instead of raising an alarm from stale data.
    /// 0 turns the rule off.
    /// </summary>
    public int AlarmPendingMaxGapSeconds { get; set; } = 10;

    /// <summary>Closed (cleared) alarms kept in memory for the alarm list.</summary>
    public int RecentAlarmHistory { get; set; } = 200;
}

public sealed class ModbusOptions
{
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5483;

    public int PollIntervalMs { get; set; } = 500;

    public int ConnectTimeoutMs { get; set; } = 1000;

    public int ResponseTimeoutMs { get; set; } = 1500;

    public int ReconnectInitialMs { get; set; } = 500;

    public int ReconnectMaxMs { get; set; } = 10_000;
}

public sealed class NmeaOptions
{
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5484;

    /// <summary>No sentence for this long on an open connection means the talker is dead: drop the connection and reconnect.</summary>
    public int StaleAfterSeconds { get; set; } = 10;

    public int ConnectTimeoutMs { get; set; } = 1000;

    public int ReconnectInitialMs { get; set; } = 500;

    public int ReconnectMaxMs { get; set; } = 10_000;
}

// ---------- wire contract (REST and the MonitoringHub share these) ----------

public enum ConnectionState
{
    [JsonStringEnumMemberName("connected")] Connected,
    [JsonStringEnumMemberName("connecting")] Connecting,
    [JsonStringEnumMemberName("disconnected")] Disconnected,

    /// <summary>The device answers, but refused one or more register ranges (a mapping mismatch): those tags are stale, the others keep flowing.</summary>
    [JsonStringEnumMemberName("degraded")] Degraded,

    /// <summary>Turned off by configuration (Monitoring:Enabled, Monitoring:Modbus:Enabled ...).</summary>
    [JsonStringEnumMemberName("disabled")] Disabled,
}

public static class ValueQuality
{
    public const string Good = "good";

    /// <summary>The link to the source is down; the value is the last one received.</summary>
    public const string Stale = "stale";
}

/// <param name="Name">"modbus" or "nmea".</param>
/// <param name="Since">When the current state began.</param>
/// <param name="Attempts">Failed connection attempts since the last success.</param>
public sealed record ConnectionStatusDto(string Name, ConnectionState State, DateTime Since, string? LastError, int Attempts, string? Endpoint);

public sealed record LiveValueDto(string Tag, double Value, string? Unit, string Quality, DateTime Timestamp);

/// <summary>One <c>values</c> hub event: every tag that changed since the previous batch.</summary>
public sealed record ValuesBatchDto(DateTime Timestamp, IReadOnlyList<LiveValueDto> Values);

/// <param name="Valid">The receiver reports a fix (RMC status A). Latitude/longitude/speed/course are null without one.</param>
/// <param name="HeadingDeg">True heading from HDT.</param>
/// <param name="Timestamp">UTC time carried by the last RMC.</param>
public sealed record NavigationDto(bool Valid, double? Latitude, double? Longitude, double? SpeedKnots, double? CourseDeg, double? HeadingDeg, DateTime? Timestamp);

public sealed record AlarmDto(
    int Id,
    int ProjectId,
    string Tag,
    AlarmLevel Level,
    double LimitValue,
    double DelayS,
    double Hysteresis,
    string? Unit,
    string? Description,
    AlarmState State,
    DateTime RaisedAt,
    double RaisedValue,
    double PeakValue,
    DateTime? AcknowledgedAt,
    string? AcknowledgedBy,
    DateTime? ReturnedAt,
    double? ReturnedValue,
    DateTime? ClearedAt)
{
    public static AlarmDto From(Alarm a) => new(
        a.Id, a.ProjectId, a.TagName, a.Level, a.LimitValue, a.DelayS, a.Hysteresis, a.Unit, a.Description, a.State,
        a.RaisedAt, a.RaisedValue, a.PeakValue, a.AcknowledgedAt, a.AcknowledgedBy, a.ReturnedAt, a.ReturnedValue, a.ClearedAt);
}

/// <summary>One <c>alarm</c> hub event: an alarm changed. <paramref name="Alarm"/> is its state AFTER the change.</summary>
public sealed record AlarmNotification(AlarmEventKind Kind, AlarmDto Alarm, DateTime At, double? Value);

public sealed record MonitoringStatusDto(
    bool Enabled,
    int ProjectId,
    ConnectionStatusDto Modbus,
    ConnectionStatusDto Nmea,
    int TagCount,
    int ActiveAlarms,
    int UnacknowledgedAlarms,
    DateTime? LastValueAt);

public sealed record AlarmListDto(IReadOnlyList<AlarmDto> Items, int Active, int Unacknowledged);

/// <summary>The hub's catch-up message to a client that just connected (sent as separate events too, see MonitoringHub).</summary>
public sealed record MonitoringSnapshotDto(
    MonitoringStatusDto Status,
    IReadOnlyList<LiveValueDto> Values,
    NavigationDto? Navigation,
    IReadOnlyList<AlarmDto> Alarms);
