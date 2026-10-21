using Bosun.Domain;
using Bosun.Domain.Entities;

namespace Bosun.Application.Monitoring;

/// <summary>One configured limit of a tag, as read from the database (<see cref="AlarmLimit"/>).</summary>
public sealed record AlarmLimitSpec(AlarmLevel Level, double Value, double DelayS, double Hysteresis)
{
    public bool IsHigh => Level >= AlarmLevel.H;

    /// <summary>H/HH are in alarm above the value, L/LL below it (the value itself is not yet in alarm).</summary>
    public bool Exceeded(double v) => IsHigh ? v > Value : v < Value;

    /// <summary>The value has moved back by the hysteresis: <c>v &lt;= limit - hysteresis</c> for H/HH, <c>v &gt;= limit + hysteresis</c> for L/LL.</summary>
    public bool Recovered(double v) => IsHigh ? v <= Value - Hysteresis : v >= Value + Hysteresis;
}

public sealed record TagAlarmConfig(string Tag, string? Unit, string? Description, IReadOnlyList<AlarmLimitSpec> Limits);

/// <summary>A change of one alarm. <see cref="Alarm"/> is a COPY of the alarm as it is after the change.</summary>
public sealed record AlarmTransition(AlarmEventKind Kind, Alarm Alarm, DateTime At, double? Value, string? Actor, string? Note);

public enum AckOutcome
{
    Acknowledged,
    NotOpen,
    AlreadyAcknowledged,
}

public sealed record AckResult(AckOutcome Outcome, Alarm? Alarm, IReadOnlyList<AlarmTransition> Transitions);

/// <summary>
/// The alarm state machine. Pure and deterministic: time is passed in, nothing is stored, nobody is notified, so it can be
/// tested exhaustively; <see cref="AlarmManager"/> owns the locking, persistence and notifications around it. NOT thread-safe.
/// <para>
/// Per (tag, level) a tracker moves Normal -> Pending -> Active -> Normal:
/// the value must be beyond the limit CONTINUOUSLY for <c>DelayS</c> seconds before the alarm is raised (one sample back inside
/// resets the timer); once raised it only ends when the value has moved back by the hysteresis, so a value hovering at the limit
/// cannot make the alarm chatter. Each limit level (LL, L, H, HH) is its own alarm.
/// </para>
/// <para>
/// Alarm states (see <see cref="AlarmState"/>): raised = <c>unacknowledged</c>; ack = <c>acknowledged</c>; the condition going away
/// while acknowledged = <c>cleared</c>; going away while still unacknowledged = <c>returned_unacknowledged</c>, which stays open until
/// the operator acknowledges it (then <c>cleared</c>). A condition that comes back after that starts a NEW alarm.
/// </para>
/// <para>
/// Delays are measured in the wall-clock time of the SAMPLES (the timestamp passed to <see cref="Evaluate"/>), not in the number
/// of samples. A pending delay is only valid across an unbroken run of samples: when a tag gets no sample for longer than
/// <c>maxPendingGap</c>, or the link is reported lost (<see cref="ResetPending"/>), the pending delay is discarded and starts again
/// at the next sample, so one sample after an outage never raises an alarm on the strength of data from before the outage. An
/// ACTIVE alarm is left as it is during a gap (nothing is known about the value; the live values are shown stale meanwhile).
/// </para>
/// </summary>
public sealed class AlarmEvaluator(int projectId, Func<int> nextId, TimeSpan? maxPendingGap = null)
{
    private enum Phase
    {
        Normal,
        Pending,
        Active,
    }

    private sealed class Tracker(AlarmLimitSpec spec)
    {
        public AlarmLimitSpec Spec { get; set; } = spec;

        public Phase Phase { get; set; }

        public DateTime PendingSince { get; set; }

        public DateTime LastSampleAt { get; set; }

        public Alarm? Alarm { get; set; }
    }

    private readonly Dictionary<(string Tag, AlarmLevel Level), Tracker> trackers = [];
    private readonly Dictionary<string, TagAlarmConfig> configs = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Alarm> open = [];

    /// <summary>Alarms that are not closed yet (unacknowledged, acknowledged, returned unacknowledged), copies.</summary>
    public IReadOnlyList<Alarm> OpenAlarms => [.. open.Values.Select(a => a.Clone())];

    public bool HasLimits(string tag) => configs.ContainsKey(tag);

    /// <summary>Re-adopts alarms that were open when the process stopped. Call before the first <see cref="SetLimits"/>.</summary>
    public void Restore(IEnumerable<Alarm> alarms)
    {
        foreach (var alarm in alarms)
        {
            if (alarm.State == AlarmState.Cleared)
            {
                continue;
            }

            open[alarm.Id] = alarm;
            if (alarm.State is AlarmState.Unacknowledged or AlarmState.Acknowledged)
            {
                var spec = new AlarmLimitSpec(alarm.Level, alarm.LimitValue, alarm.DelayS, alarm.Hysteresis);
                trackers[(alarm.TagName, alarm.Level)] = new Tracker(spec) { Phase = Phase.Active, Alarm = alarm };
            }
        }
    }

    /// <summary>
    /// Replaces the configured limits. A limit that disappeared ends its alarm like a return to normal; a limit whose numbers changed
    /// restarts a pending delay, and an active alarm is judged against the NEW numbers from the next sample on. The active alarm's
    /// row shows the new numbers and a <see cref="AlarmEventKind.LimitChanged"/> event is published and stored (note: old -> new).
    /// </summary>
    public IReadOnlyList<AlarmTransition> SetLimits(IEnumerable<TagAlarmConfig> limits, DateTime now)
    {
        var transitions = new List<AlarmTransition>();
        var wanted = new Dictionary<(string, AlarmLevel), AlarmLimitSpec>();
        var next = new Dictionary<string, TagAlarmConfig>(StringComparer.Ordinal);
        foreach (var config in limits)
        {
            next[config.Tag] = config;
            foreach (var spec in config.Limits)
            {
                wanted[(config.Tag, spec.Level)] = Sanitise(spec);
            }
        }

        foreach (var key in trackers.Keys.ToList())
        {
            if (wanted.ContainsKey(key))
            {
                continue;
            }

            var tracker = trackers[key];
            if (tracker is { Phase: Phase.Active, Alarm: { } alarm })
            {
                Return(alarm, tracker, value: null, now, "limit removed", transitions);
            }

            trackers.Remove(key);
        }

        foreach (var (key, spec) in wanted)
        {
            if (!trackers.TryGetValue(key, out var tracker))
            {
                trackers[key] = new Tracker(spec);
                continue;
            }

            if (tracker.Spec == spec)
            {
                continue;
            }

            tracker.Spec = spec;
            if (tracker.Phase == Phase.Pending)
            {
                tracker.Phase = Phase.Normal; // the delay was counted against the old limit
            }
            else if (tracker is { Phase: Phase.Active, Alarm: { } active })
            {
                var note = $"limit {active.LimitValue:G} -> {spec.Value:G}, delay {active.DelayS:G} -> {spec.DelayS:G} s, hysteresis {active.Hysteresis:G} -> {spec.Hysteresis:G}";
                active.LimitValue = spec.Value;
                active.DelayS = spec.DelayS;
                active.Hysteresis = spec.Hysteresis;
                transitions.Add(new AlarmTransition(AlarmEventKind.LimitChanged, active.Clone(), now, null, null, note));
            }
        }

        configs.Clear();
        foreach (var (tag, config) in next)
        {
            configs[tag] = config;
        }

        return transitions;
    }

    /// <summary>Feeds one sample of one tag; returns what changed.</summary>
    public IReadOnlyList<AlarmTransition> Evaluate(string tag, double value, DateTime now)
    {
        var transitions = new List<AlarmTransition>();
        if (!configs.TryGetValue(tag, out var config) || !double.IsFinite(value))
        {
            return transitions;
        }

        foreach (var limit in config.Limits)
        {
            if (!trackers.TryGetValue((tag, limit.Level), out var tracker))
            {
                continue;
            }

            var spec = tracker.Spec;
            switch (tracker.Phase)
            {
                case Phase.Normal:
                    if (spec.Exceeded(value))
                    {
                        tracker.Phase = Phase.Pending;
                        tracker.PendingSince = now;
                        tracker.LastSampleAt = now;
                        TryRaise(tracker, config, value, now, transitions);
                    }

                    break;

                case Phase.Pending:
                    if (maxPendingGap is { } gap && gap > TimeSpan.Zero && now - tracker.LastSampleAt > gap)
                    {
                        tracker.PendingSince = now; // a hole in the samples: the delay starts again from this sample
                    }

                    tracker.LastSampleAt = now;
                    if (!spec.Exceeded(value))
                    {
                        tracker.Phase = Phase.Normal;
                    }
                    else
                    {
                        TryRaise(tracker, config, value, now, transitions);
                    }

                    break;

                case Phase.Active:
                    var alarm = tracker.Alarm!;
                    alarm.PeakValue = spec.IsHigh ? Math.Max(alarm.PeakValue, value) : Math.Min(alarm.PeakValue, value);
                    if (spec.Recovered(value))
                    {
                        Return(alarm, tracker, value, now, null, transitions);
                    }

                    break;
            }
        }

        return transitions;
    }

    /// <summary>The source of samples went away: every pending delay is discarded (active alarms are untouched).</summary>
    public void ResetPending()
    {
        foreach (var tracker in trackers.Values)
        {
            if (tracker.Phase == Phase.Pending)
            {
                tracker.Phase = Phase.Normal;
            }
        }
    }

    /// <summary>An operator acknowledges alarm <paramref name="id"/>.</summary>
    public AckResult Acknowledge(int id, string actor, DateTime now)
    {
        if (!open.TryGetValue(id, out var alarm))
        {
            return new AckResult(AckOutcome.NotOpen, null, []);
        }

        if (alarm.State == AlarmState.Acknowledged)
        {
            return new AckResult(AckOutcome.AlreadyAcknowledged, alarm.Clone(), []);
        }

        var transitions = new List<AlarmTransition>();
        alarm.AcknowledgedAt = now;
        alarm.AcknowledgedBy = actor;
        if (alarm.State == AlarmState.Unacknowledged)
        {
            alarm.State = AlarmState.Acknowledged;
            transitions.Add(new AlarmTransition(AlarmEventKind.Acknowledged, alarm.Clone(), now, null, actor, null));
        }
        else
        {
            // returned_unacknowledged: the condition is already gone, so acknowledging closes the alarm.
            alarm.State = AlarmState.Cleared;
            alarm.ClearedAt = now;
            open.Remove(id);
            transitions.Add(new AlarmTransition(AlarmEventKind.Acknowledged, alarm.Clone(), now, null, actor, null));
            transitions.Add(new AlarmTransition(AlarmEventKind.Cleared, alarm.Clone(), now, null, actor, "acknowledged after return to normal"));
        }

        return new AckResult(AckOutcome.Acknowledged, alarm.Clone(), transitions);
    }

    private void TryRaise(Tracker tracker, TagAlarmConfig config, double value, DateTime now, List<AlarmTransition> transitions)
    {
        var spec = tracker.Spec;
        if ((now - tracker.PendingSince).TotalSeconds < spec.DelayS)
        {
            return;
        }

        var alarm = new Alarm
        {
            Id = nextId(),
            ProjectId = projectId,
            TagName = config.Tag,
            Level = spec.Level,
            LimitValue = spec.Value,
            DelayS = spec.DelayS,
            Hysteresis = spec.Hysteresis,
            Unit = config.Unit,
            Description = config.Description,
            State = AlarmState.Unacknowledged,
            RaisedAt = now,
            RaisedValue = value,
            PeakValue = value,
        };
        open[alarm.Id] = alarm;
        tracker.Phase = Phase.Active;
        tracker.Alarm = alarm;
        transitions.Add(new AlarmTransition(AlarmEventKind.Raised, alarm.Clone(), now, value, null, null));
    }

    /// <summary>The condition went away: acknowledged alarms close, unacknowledged ones wait for the operator.</summary>
    private void Return(Alarm alarm, Tracker tracker, double? value, DateTime now, string? note, List<AlarmTransition> transitions)
    {
        alarm.ReturnedAt = now;
        alarm.ReturnedValue = value;
        tracker.Phase = Phase.Normal;
        tracker.Alarm = null;
        if (alarm.State == AlarmState.Acknowledged)
        {
            alarm.State = AlarmState.Cleared;
            alarm.ClearedAt = now;
            open.Remove(alarm.Id);
            transitions.Add(new AlarmTransition(AlarmEventKind.ReturnedToNormal, alarm.Clone(), now, value, null, note));
            transitions.Add(new AlarmTransition(AlarmEventKind.Cleared, alarm.Clone(), now, value, null, note));
        }
        else
        {
            alarm.State = AlarmState.ReturnedUnacknowledged;
            transitions.Add(new AlarmTransition(AlarmEventKind.ReturnedToNormal, alarm.Clone(), now, value, null, note));
        }
    }

    private static AlarmLimitSpec Sanitise(AlarmLimitSpec spec) =>
        spec with { DelayS = Math.Max(0, spec.DelayS), Hysteresis = Math.Max(0, spec.Hysteresis) };
}
