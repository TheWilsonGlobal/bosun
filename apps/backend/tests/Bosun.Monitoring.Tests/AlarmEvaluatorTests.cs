using Bosun.Application.Monitoring;
using Bosun.Domain;
using Bosun.Domain.Entities;

namespace Bosun.Monitoring.Tests;

public class AlarmEvaluatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private int ids;

    private static TagAlarmConfig Tag(params AlarmLimitSpec[] limits) => new("T-1", "C", "test tag", limits);

    private static AlarmLimitSpec H(double value = 95, double delay = 10, double hysteresis = 2) => new(AlarmLevel.H, value, delay, hysteresis);

    private AlarmEvaluator Make(params AlarmLimitSpec[] limits)
    {
        var evaluator = new AlarmEvaluator(1, () => ++ids);
        evaluator.SetLimits([Tag(limits)], T0);
        return evaluator;
    }

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Delay_not_elapsed_raises_nothing_and_a_dip_restarts_the_delay()
    {
        var e = Make(H());
        e.Evaluate("T-1", 96, At(0)).Should().BeEmpty();
        e.Evaluate("T-1", 96, At(9.9)).Should().BeEmpty();
        e.Evaluate("T-1", 94, At(10)).Should().BeEmpty("one sample back inside resets the pending delay");
        e.Evaluate("T-1", 96, At(11)).Should().BeEmpty();
        e.Evaluate("T-1", 96, At(20.9)).Should().BeEmpty("only 9.9 s since the restart");
        e.OpenAlarms.Should().BeEmpty();
        var raised = e.Evaluate("T-1", 97, At(21));
        raised.Should().ContainSingle().Which.Kind.Should().Be(AlarmEventKind.Raised);
        raised[0].Alarm.State.Should().Be(AlarmState.Unacknowledged);
        raised[0].Alarm.LimitValue.Should().Be(95);
    }

    [Fact]
    public void Value_exactly_at_the_limit_is_not_in_alarm()
    {
        var e = Make(H(delay: 0));
        e.Evaluate("T-1", 95, At(0)).Should().BeEmpty();
        e.Evaluate("T-1", 95.01, At(1)).Should().ContainSingle();
    }

    [Fact]
    public void Hysteresis_keeps_the_alarm_until_the_value_has_moved_back()
    {
        var e = Make(H(delay: 0, hysteresis: 2));
        var raised = e.Evaluate("T-1", 96, At(0)).Single();
        e.Evaluate("T-1", 94.5, At(1)).Should().BeEmpty("below the limit but inside the hysteresis band");
        e.Evaluate("T-1", 93.1, At(2)).Should().BeEmpty();
        e.Evaluate("T-1", 98, At(3)).Should().BeEmpty("still the same alarm, peak is tracked");
        var back = e.Evaluate("T-1", 93, At(4));
        back.Select(t => t.Kind).Should().Equal(AlarmEventKind.ReturnedToNormal);
        back[0].Alarm.State.Should().Be(AlarmState.ReturnedUnacknowledged, "unacknowledged alarms wait for the operator");
        back[0].Alarm.PeakValue.Should().Be(98);
        back[0].Alarm.Id.Should().Be(raised.Alarm.Id);
        e.OpenAlarms.Should().ContainSingle();
    }

    [Fact]
    public void Ack_then_return_to_normal_clears()
    {
        var e = Make(H(delay: 0, hysteresis: 1));
        var id = e.Evaluate("T-1", 96, At(0)).Single().Alarm.Id;
        var ack = e.Acknowledge(id, "alice", At(1));
        ack.Outcome.Should().Be(AckOutcome.Acknowledged);
        ack.Alarm!.State.Should().Be(AlarmState.Acknowledged);
        ack.Alarm.AcknowledgedBy.Should().Be("alice");
        e.Acknowledge(id, "alice", At(2)).Outcome.Should().Be(AckOutcome.AlreadyAcknowledged);

        var back = e.Evaluate("T-1", 90, At(3));
        back.Select(t => t.Kind).Should().Equal(AlarmEventKind.ReturnedToNormal, AlarmEventKind.Cleared);
        back[1].Alarm.State.Should().Be(AlarmState.Cleared);
        e.OpenAlarms.Should().BeEmpty();
        e.Acknowledge(id, "alice", At(4)).Outcome.Should().Be(AckOutcome.NotOpen);
    }

    [Fact]
    public void Return_then_ack_clears_and_a_new_excursion_is_a_new_alarm()
    {
        var e = Make(H(delay: 0, hysteresis: 1));
        var first = e.Evaluate("T-1", 96, At(0)).Single().Alarm.Id;
        e.Evaluate("T-1", 90, At(1)).Should().ContainSingle();
        var ack = e.Acknowledge(first, "bob", At(2));
        ack.Transitions.Select(t => t.Kind).Should().Equal(AlarmEventKind.Acknowledged, AlarmEventKind.Cleared);
        ack.Alarm!.State.Should().Be(AlarmState.Cleared);

        var second = e.Evaluate("T-1", 97, At(3)).Single().Alarm.Id;
        second.Should().NotBe(first);
    }

    [Fact]
    public void Low_limits_work_below_and_levels_are_independent_alarms()
    {
        var e = Make(new AlarmLimitSpec(AlarmLevel.L, 2.0, 0, 0.1), new AlarmLimitSpec(AlarmLevel.LL, 1.0, 0, 0.1));
        e.Evaluate("T-1", 1.9, At(0)).Should().ContainSingle().Which.Alarm.Level.Should().Be(AlarmLevel.L);
        e.Evaluate("T-1", 0.9, At(1)).Should().ContainSingle().Which.Alarm.Level.Should().Be(AlarmLevel.LL);
        e.OpenAlarms.Should().HaveCount(2);
        e.Evaluate("T-1", 2.05, At(2)).Should().Contain(t => t.Alarm.Level == AlarmLevel.LL, "LL recovers once above 1.1");
    }

    [Fact]
    public void Unconfigured_tags_and_non_finite_values_are_ignored()
    {
        var e = Make(H(delay: 0));
        e.Evaluate("OTHER", 500, At(0)).Should().BeEmpty();
        e.Evaluate("T-1", double.NaN, At(0)).Should().BeEmpty();
    }

    [Fact]
    public void A_changed_limit_takes_effect_and_restarts_a_pending_delay()
    {
        var e = Make(H(value: 120, delay: 5));
        e.Evaluate("T-1", 100, At(0)).Should().BeEmpty("100 is below the old limit of 120");

        e.SetLimits([Tag(H(value: 95, delay: 5))], At(1));
        e.Evaluate("T-1", 100, At(2)).Should().BeEmpty("pending starts now");
        e.Evaluate("T-1", 100, At(6.9)).Should().BeEmpty();
        e.Evaluate("T-1", 100, At(7)).Should().ContainSingle();
    }

    [Fact]
    public void Removing_the_limit_ends_an_active_alarm_and_a_raised_limit_is_judged_from_the_next_sample()
    {
        var e = Make(H(delay: 0, hysteresis: 1));
        e.Evaluate("T-1", 96, At(0)).Should().ContainSingle();

        // limit raised to 100: 96 is now below 100 - hysteresis, so the alarm returns on the next sample
        e.SetLimits([Tag(H(value: 100, delay: 0, hysteresis: 1))], At(1)).Should().BeEmpty();
        e.Evaluate("T-1", 96, At(2)).Select(t => t.Kind).Should().Equal(AlarmEventKind.ReturnedToNormal);

        var e2 = Make(H(delay: 0));
        e2.Evaluate("T-1", 96, At(0));
        var ended = e2.SetLimits([Tag()], At(1));
        ended.Should().ContainSingle().Which.Note.Should().Be("limit removed");
    }

    [Fact]
    public void Open_alarms_are_restored_after_a_restart_without_raising_a_duplicate()
    {
        var e = new AlarmEvaluator(1, () => ++ids);
        var stored = new Alarm { Id = 41, ProjectId = 1, TagName = "T-1", Level = AlarmLevel.H, LimitValue = 95, Hysteresis = 1, State = AlarmState.Unacknowledged, RaisedAt = T0, RaisedValue = 96, PeakValue = 96 };
        e.Restore([stored]);
        e.SetLimits([Tag(H(delay: 0, hysteresis: 1))], T0);
        e.Evaluate("T-1", 97, At(1)).Should().BeEmpty("the alarm is already open");
        e.OpenAlarms.Should().ContainSingle().Which.Id.Should().Be(41);
        e.Acknowledge(41, "carol", At(2)).Outcome.Should().Be(AckOutcome.Acknowledged);
    }
}
