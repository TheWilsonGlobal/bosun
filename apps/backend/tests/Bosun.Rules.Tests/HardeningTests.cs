using Bosun.Domain;
using Bosun.Rules;

namespace Bosun.Rules.Tests;

/// <summary>Reviewer findings on phases 0-2: undefined enums, channel/signal consistency, default known systems.</summary>
public class HardeningTests
{
    private static FakeProject Project() => new FakeProject()
        .WithTag(Tags.Analog("ME-EXH-TT01", b => b.Channel = 10))
        .WithTag(Tags.Analog("ME-EXH-TT02", b => b.Channel = 99)) // channel the view does not know
        .WithTag(Tags.Analog("ME-EXH-TT03"))
        .WithChannel(10, SignalType.AI, "ME-EXH-TT01")
        .WithChannel(11, SignalType.AO);

    private static EvaluationResult Evaluate(params ChangeOp[] ops) => ChangeSetEvaluator.Evaluate(ops, Project());

    private static UpdateTagOp UpdateOp(string tag, string field, string? value) => new() { TagName = tag, Field = field, New = value };

    [Theory]
    [InlineData(99, 0, "signalType")]
    [InlineData(0, 99, "safetyClass")]
    public void Create_with_an_undefined_enum_is_rejected_and_never_reaches_the_final_state(int signal, int safety, string field)
    {
        var result = Evaluate(new CreateTagOp
        {
            TagName = "AE-DG1-TT01",
            SignalType = (SignalType)signal,
            SafetyClass = (SafetyClass)safety,
            Unit = "°C",
            RangeLow = 0,
            RangeHigh = 100,
        });

        result.HasErrors.Should().BeTrue();
        result.Violations.Should().Contain(v => v.Code == ViolationCodes.InvalidValue && v.Field == field && v.TagName == "AE-DG1-TT01");
        result.FinalState.Should().NotContainKey("AE-DG1-TT01");
    }

    [Fact]
    public void Set_alarm_limit_with_an_undefined_level_is_rejected()
    {
        var result = Evaluate(new SetAlarmLimitOp { TagName = "ME-EXH-TT03", Level = (AlarmLevel)7, New = new AlarmLimitValue(90, 1, 1) });

        result.Violations.Should().Contain(v => v.Code == ViolationCodes.InvalidValue && v.Field == "level");
        result.FinalState.Should().NotContainKey("ME-EXH-TT03");
    }

    [Fact]
    public void Changing_signal_type_of_a_channel_bound_tag_to_another_type_is_a_mismatch()
    {
        var result = Evaluate(UpdateOp("ME-EXH-TT01", "signalType", "DI"));

        result.Violations.Should().Contain(v => v.Code == ViolationCodes.ChannelSignalMismatch && v.Field == "signalType" && v.Severity == Severity.Error);
    }

    [Fact]
    public void Changing_signal_type_to_the_channels_own_type_is_fine()
    {
        Evaluate(UpdateOp("ME-EXH-TT01", "signalType", "AI")).Violations.Should().NotContain(v => v.Code == ViolationCodes.ChannelSignalMismatch);
    }

    [Fact]
    public void The_signal_type_check_ignores_unbound_tags_unknown_channels_and_other_fields()
    {
        string[] Codes(ChangeOp op) => Evaluate(op).Violations.Select(v => v.Code).ToArray();

        Codes(UpdateOp("ME-EXH-TT03", "signalType", "DI")).Should().NotContain(ViolationCodes.ChannelSignalMismatch); // not bound
        Codes(UpdateOp("ME-EXH-TT02", "signalType", "DI")).Should().NotContain(ViolationCodes.ChannelSignalMismatch); // channel not in the view
        Codes(UpdateOp("ME-EXH-TT01", "description", "new text")).Should().NotContain(ViolationCodes.ChannelSignalMismatch); // other field
    }

    [Fact]
    public void Bind_then_retype_in_one_change_set_is_checked_against_the_new_channel()
    {
        var result = Evaluate(
            new BindChannelOp { TagName = "ME-EXH-TT03", New = 11 }.WithOldNull(),
            UpdateOp("ME-EXH-TT03", "signalType", "AO"));
        result.Violations.Should().Contain(v => v.Code == ViolationCodes.ChannelSignalMismatch && v.Field == "channelId"); // AI tag on an AO channel

        var retypeFirst = Evaluate(
            UpdateOp("ME-EXH-TT03", "signalType", "AO"),
            new BindChannelOp { TagName = "ME-EXH-TT03", New = 11 });
        retypeFirst.Violations.Should().NotContain(v => v.Code == ViolationCodes.ChannelSignalMismatch);
    }

    [Theory]
    [InlineData("ME")]
    [InlineData("AE")]
    [InlineData("BLR")]
    [InlineData("PMS")]
    [InlineData("BWTS")]
    [InlineData("TANK")]
    public void The_default_known_systems_cover_every_system_the_demo_project_uses(string system)
    {
        RuleOptions.Default.KnownSystems.Should().Contain(system);
        TagRules.Validate(Tags.Analog($"{system}-EXH-TT01")).Should().NotContain(v => v.Code == ViolationCodes.UnknownSystem);
    }

    [Fact]
    public void An_unlisted_system_is_a_warning_by_default_and_disabling_the_list_silences_it()
    {
        var violations = TagRules.Validate(Tags.Analog("ZZ-EXH-TT01"));
        violations.Should().ContainSingle(v => v.Code == ViolationCodes.UnknownSystem).Which.Severity.Should().Be(Severity.Warning);

        TagRules.Validate(Tags.Analog("ZZ-EXH-TT01"), new RuleOptions { KnownSystems = null }).Should().BeEmpty();
    }
}

internal static class OpExtensions
{
    /// <summary>Explicit, matching "old" for a bind op on an unbound tag (the property is already null; this reads better at call sites).</summary>
    public static BindChannelOp WithOldNull(this BindChannelOp op) => op with { Old = null };
}
