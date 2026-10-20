using Bosun.Domain;
using Bosun.Rules;

namespace Bosun.Rules.Tests;

public class ChangeSetEvaluatorTests
{
    private static FakeProject Project() => new FakeProject()
        .WithTag(Tags.Analog("ME-EXH-TT01"))
        .WithTag(Tags.Analog("ME-EXH-TT02"))
        .WithTag(Tags.Analog("ME-SPD-ST01", b => { b.Signal = SignalType.AI; b.Unit = "rpm"; b.High = 150; b.Safety = SafetyClass.SIL; }))
        .WithTag(Tags.Analog("ME-RUN-XS01", b => { b.Signal = SignalType.DI; b.Unit = null; b.Low = null; b.High = null; }))
        .WithGroup("ME-EXH")
        .WithChannel(1, SignalType.AI)
        .WithChannel(2, SignalType.AI, "ME-EXH-TT02")
        .WithChannel(3, SignalType.DI);

    private static CreateTagOp Create(string name = "AE-DG1-TT01") => new() { TagName = name, SignalType = SignalType.AI, Unit = "°C", RangeLow = 0, RangeHigh = 100 };

    private static EvaluationResult Evaluate(params ChangeOp[] ops) => ChangeSetEvaluator.Evaluate(ops, Project());

    private static string[] ErrorCodes(EvaluationResult r) => r.Violations.Where(v => v.Severity == Severity.Error).Select(v => v.Code).ToArray();

    [Fact]
    public void Valid_change_set_has_no_errors_and_exposes_final_state()
    {
        var result = Evaluate(
            new SetAlarmLimitOp { TagName = "ME-EXH-TT01", Level = AlarmLevel.H, New = new AlarmLimitValue(95, 10, 1) },
            new AssignGroupOp { TagName = "ME-EXH-TT01", New = "ME-EXH" });

        result.HasErrors.Should().BeFalse();
        result.RequiresSecondApprover.Should().BeFalse();
        result.TouchedTags.Should().Equal("ME-EXH-TT01");
        result.FinalState["ME-EXH-TT01"]!.Limits[AlarmLevel.H].Value.Should().Be(95);
        result.FinalState["ME-EXH-TT01"]!.AlarmGroup.Should().Be("ME-EXH");
        result.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void Empty_change_set_is_an_error()
    {
        var result = Evaluate();
        ErrorCodes(result).Should().Equal(ViolationCodes.EmptyChangeSet);
    }

    [Fact]
    public void More_than_max_ops_is_an_error()
    {
        var ops = Enumerable.Range(1, 201).Select(i => (ChangeOp)new CreateTagOp
        {
            TagName = $"TANK-FO{i:000}-LT01",
            SignalType = SignalType.AI,
            Unit = "%",
            RangeLow = 0,
            RangeHigh = 100,
        }).ToList();

        var result = ChangeSetEvaluator.Evaluate(ops, Project());
        ErrorCodes(result).Should().Contain(ViolationCodes.TooManyOps);

        // 200 exactly is allowed; custom max respected
        ChangeSetEvaluator.Evaluate(ops.Take(200).ToList(), Project()).HasErrors.Should().BeFalse();
        ErrorCodes(ChangeSetEvaluator.Evaluate(ops.Take(3).ToList(), Project(), new RuleOptions { MaxOps = 2 }))
            .Should().Equal(ViolationCodes.TooManyOps);
    }

    [Fact]
    public void Create_tag_duplicates()
    {
        ErrorCodes(Evaluate(Create("ME-EXH-TT01"))).Should().Equal(ViolationCodes.DuplicateInProject);
        ErrorCodes(Evaluate(Create(), Create())).Should().Equal(ViolationCodes.DuplicateInChangeSet);
    }

    [Fact]
    public void Create_after_delete_in_same_change_set_is_allowed()
    {
        var result = Evaluate(
            new DeleteTagOp { TagName = "ME-EXH-TT01" },
            Create("ME-EXH-TT01"));

        result.HasErrors.Should().BeFalse();
        result.FinalState["ME-EXH-TT01"].Should().NotBeNull();
        result.TouchedTags.Should().Equal("ME-EXH-TT01");
    }

    [Fact]
    public void Create_validates_the_new_tag_and_the_alarm_group()
    {
        var result = Evaluate(Create("bad name") with { Unit = "furlongs", AlarmGroup = "NOPE" });
        ErrorCodes(result).Should().BeEquivalentTo(
            ViolationCodes.AlarmGroupUnknown, ViolationCodes.TagNameConvention, ViolationCodes.UnitUnknown);

        var good = Evaluate(Create() with { AlarmGroup = "ME-EXH" });
        good.HasErrors.Should().BeFalse();
        good.FinalState["AE-DG1-TT01"]!.AlarmGroup.Should().Be("ME-EXH");
    }

    [Fact]
    public void Ops_on_missing_tags_are_reported_for_every_op_type()
    {
        var result = Evaluate(
            new UpdateTagOp { TagName = "X-A-TT01", Field = "unit", New = "bar" },
            new SetAlarmLimitOp { TagName = "X-A-TT01", Level = AlarmLevel.H, New = new AlarmLimitValue(1, 0, 0) },
            new AssignGroupOp { TagName = "X-A-TT01", New = "ME-EXH" },
            new BindChannelOp { TagName = "X-A-TT01", New = 1 },
            new DeleteTagOp { TagName = "X-A-TT01" });

        result.Violations.Should().HaveCount(5).And.OnlyContain(v => v.Code == ViolationCodes.TagNotFound && v.Severity == Severity.Error);
        result.TouchedTags.Should().BeEmpty();
        result.FinalState.Should().BeEmpty();
    }

    [Fact]
    public void Update_field_applies_value_and_checks_old()
    {
        var ok = Evaluate(new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "rangeHigh", Old = "500", New = "600" });
        ok.HasErrors.Should().BeFalse();
        ok.FinalState["ME-EXH-TT01"]!.RangeHigh.Should().Be(600);

        // "500.0" equals "500"; field names are case-insensitive
        Evaluate(new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "RANGEHIGH", Old = "500.0", New = "600" })
            .Conflicts.Should().BeEmpty();

        var stale = Evaluate(new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "rangeHigh", Old = "400", New = "600" });
        stale.Conflicts.Should().ContainSingle().Which.Should().Match<RuleViolation>(v => v.Field == "rangeHigh" && v.TagName == "ME-EXH-TT01");
        stale.FinalState["ME-EXH-TT01"]!.RangeHigh.Should().Be(600); // still simulated so later checks make sense
    }

    [Fact]
    public void Update_text_enum_and_clear()
    {
        var result = Evaluate(
            new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "description", Old = "desc", New = "Exhaust gas temperature cyl 1" },
            new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "safetyClass", Old = "none", New = "esd" },
            new UpdateTagOp { TagName = "ME-EXH-TT02", Field = "signalType", Old = "AI", New = "AO" },
            new UpdateTagOp { TagName = "ME-EXH-TT02", Field = "description", Old = "desc", New = "" });

        result.HasErrors.Should().BeFalse();
        result.FinalState["ME-EXH-TT01"]!.Description.Should().Be("Exhaust gas temperature cyl 1");
        result.FinalState["ME-EXH-TT01"]!.SafetyClass.Should().Be(SafetyClass.ESD);
        result.FinalState["ME-EXH-TT02"]!.SignalType.Should().Be(SignalType.AO);
        result.FinalState["ME-EXH-TT02"]!.Description.Should().BeNull();
        result.RequiresSecondApprover.Should().BeTrue();
    }

    [Fact]
    public void Update_clearing_a_range_makes_the_tag_invalid()
    {
        var result = Evaluate(new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "rangeLow", Old = "0", New = "" });
        ErrorCodes(result).Should().Equal(ViolationCodes.RangeMissing);
    }

    [Theory]
    [InlineData("rangeLow", "abc")]
    [InlineData("rangeLow", "NaN")]
    [InlineData("rangeHigh", "1e999")]
    [InlineData("signalType", "XX")]
    [InlineData("signalType", "7")]
    [InlineData("signalType", "")]
    [InlineData("safetyClass", "high")]
    public void Update_with_unparseable_value(string field, string value)
    {
        var result = Evaluate(new UpdateTagOp { TagName = "ME-EXH-TT01", Field = field, New = value });
        result.Violations.Should().Contain(v => v.Code == ViolationCodes.InvalidValue && v.Field == field);
        result.FinalState["ME-EXH-TT01"]!.RangeHigh.Should().Be(500);
    }

    [Fact]
    public void Update_unknown_field()
    {
        var result = Evaluate(new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "colour", New = "red" });
        result.Violations.Should().ContainSingle(v => v.Code == ViolationCodes.UnknownField).Which.Message.Should().Contain("rangeHigh");
    }

    [Fact]
    public void Set_alarm_limit_checks_old_and_supports_removal()
    {
        var created = Evaluate(new SetAlarmLimitOp { TagName = "ME-EXH-TT01", Level = AlarmLevel.H, New = new AlarmLimitValue(95, 10, 1) });
        created.Conflicts.Should().BeEmpty();

        var stale = Evaluate(new SetAlarmLimitOp { TagName = "ME-EXH-TT01", Level = AlarmLevel.H, Old = new AlarmLimitValue(1, 1, 1), New = new AlarmLimitValue(95, 10, 1) });
        stale.Conflicts.Should().ContainSingle().Which.Field.Should().Be("limits.H");

        var removal = ChangeSetEvaluator.Evaluate(
            [new SetAlarmLimitOp { TagName = "ME-EXH-TT01", Level = AlarmLevel.H, Old = new AlarmLimitValue(95, 10, 1), New = null }],
            new FakeProject().WithTag(Tags.Analog("ME-EXH-TT01", b => b.Limits = Tags.Limits((AlarmLevel.H, 95)).SetItem(AlarmLevel.H, new AlarmLimitValue(95, 10, 1)))));
        removal.HasErrors.Should().BeFalse();
        removal.FinalState["ME-EXH-TT01"]!.Limits.Should().BeEmpty();
    }

    [Fact]
    public void Set_alarm_limit_results_are_rule_checked()
    {
        var result = Evaluate(
            new SetAlarmLimitOp { TagName = "ME-EXH-TT01", Level = AlarmLevel.H, New = new AlarmLimitValue(95, 10, 1) },
            new SetAlarmLimitOp { TagName = "ME-EXH-TT01", Level = AlarmLevel.L, New = new AlarmLimitValue(99, -1, 1) });
        ErrorCodes(result).Should().BeEquivalentTo(ViolationCodes.LimitOrder, ViolationCodes.DelayInvalid);
    }

    [Fact]
    public void Assign_group()
    {
        Evaluate(new AssignGroupOp { TagName = "ME-EXH-TT01", New = "ME-EXH" }).HasErrors.Should().BeFalse();
        Evaluate(new AssignGroupOp { TagName = "ME-EXH-TT01", Old = null, New = null }).HasErrors.Should().BeFalse();
        ErrorCodes(Evaluate(new AssignGroupOp { TagName = "ME-EXH-TT01", New = "NOPE" })).Should().Equal(ViolationCodes.AlarmGroupUnknown);
        Evaluate(new AssignGroupOp { TagName = "ME-EXH-TT01", Old = "OTHER", New = "ME-EXH" })
            .Conflicts.Should().ContainSingle().Which.Field.Should().Be("alarmGroup");

        var grouped = new FakeProject().WithTag(Tags.Analog("ME-EXH-TT01", b => b.Group = "ME-EXH")).WithGroup("ME-EXH");
        ChangeSetEvaluator.Evaluate([new AssignGroupOp { TagName = "ME-EXH-TT01", Old = "ME-EXH", New = null }], grouped).HasErrors.Should().BeFalse();
        ChangeSetEvaluator.Evaluate([new AssignGroupOp { TagName = "ME-EXH-TT01", Old = null, New = null }], grouped)
            .Conflicts.Should().ContainSingle();
    }

    [Fact]
    public void Bind_channel()
    {
        var ok = Evaluate(new BindChannelOp { TagName = "ME-EXH-TT01", New = 1 });
        ok.HasErrors.Should().BeFalse();
        ok.FinalState["ME-EXH-TT01"]!.ChannelId.Should().Be(1);

        ErrorCodes(Evaluate(new BindChannelOp { TagName = "ME-EXH-TT01", New = 99 })).Should().Equal(ViolationCodes.ChannelUnknown);
        ErrorCodes(Evaluate(new BindChannelOp { TagName = "ME-EXH-TT01", New = 3 })).Should().Equal(ViolationCodes.ChannelSignalMismatch);
        ErrorCodes(Evaluate(new BindChannelOp { TagName = "ME-EXH-TT01", New = 2 })).Should().Equal(ViolationCodes.ChannelInUse);
        Evaluate(new BindChannelOp { TagName = "ME-EXH-TT01", Old = 5, New = 1 }).Conflicts.Should().ContainSingle().Which.Field.Should().Be("channelId");

        // a failed bind leaves the binding untouched
        Evaluate(new BindChannelOp { TagName = "ME-EXH-TT01", New = 99 }).FinalState["ME-EXH-TT01"]!.ChannelId.Should().BeNull();
    }

    [Fact]
    public void Rebinding_the_same_channel_and_moving_channels_within_a_change_set()
    {
        var project = new FakeProject()
            .WithTag(Tags.Analog("ME-EXH-TT01", b => b.Channel = 2))
            .WithTag(Tags.Analog("ME-EXH-TT02"))
            .WithChannel(2, SignalType.AI, "ME-EXH-TT01")
            .WithChannel(4, SignalType.AI);

        // same tag, same channel: not "in use"
        ChangeSetEvaluator.Evaluate([new BindChannelOp { TagName = "ME-EXH-TT01", Old = 2, New = 2 }], project).HasErrors.Should().BeFalse();

        // unbind TT01 (frees channel 2), then bind TT02 to it
        var moved = ChangeSetEvaluator.Evaluate(
            [new BindChannelOp { TagName = "ME-EXH-TT01", Old = 2, New = null }, new BindChannelOp { TagName = "ME-EXH-TT02", New = 2 }],
            project);
        moved.HasErrors.Should().BeFalse();
        moved.FinalState["ME-EXH-TT01"]!.ChannelId.Should().BeNull();
        moved.FinalState["ME-EXH-TT02"]!.ChannelId.Should().Be(2);

        // binding TT02 to channel 2 while TT01 still owns it
        ErrorCodes(ChangeSetEvaluator.Evaluate([new BindChannelOp { TagName = "ME-EXH-TT02", New = 2 }], project))
            .Should().Equal(ViolationCodes.ChannelInUse);

        // move TT01 from channel 2 to 4: channel 2 frees, 4 is taken
        var swap = ChangeSetEvaluator.Evaluate(
            [new BindChannelOp { TagName = "ME-EXH-TT01", Old = 2, New = 4 }, new BindChannelOp { TagName = "ME-EXH-TT02", New = 2 }],
            project);
        swap.HasErrors.Should().BeFalse();
        ErrorCodes(ChangeSetEvaluator.Evaluate(
            [new BindChannelOp { TagName = "ME-EXH-TT01", Old = 2, New = 4 }, new BindChannelOp { TagName = "ME-EXH-TT02", New = 4 }], project))
            .Should().Equal(ViolationCodes.ChannelInUse);
    }

    [Fact]
    public void Delete_tag()
    {
        var snapshot = Project().GetTag("ME-EXH-TT01")!;
        var ok = Evaluate(new DeleteTagOp { TagName = "ME-EXH-TT01", Old = snapshot });
        ok.HasErrors.Should().BeFalse();
        ok.FinalState["ME-EXH-TT01"].Should().BeNull();
        ok.TouchedTags.Should().Equal("ME-EXH-TT01");

        Evaluate(new DeleteTagOp { TagName = "ME-EXH-TT01" }).HasErrors.Should().BeFalse();
        Evaluate(new DeleteTagOp { TagName = "ME-EXH-TT01", Old = snapshot with { Description = "changed" } })
            .Conflicts.Should().ContainSingle();
    }

    [Fact]
    public void Delete_tag_with_alarms_warns_and_frees_the_channel_and_double_delete_fails()
    {
        var project = new FakeProject()
            .WithTag(Tags.Analog("ME-EXH-TT01", b => { b.Channel = 2; b.Limits = Tags.Limits((AlarmLevel.H, 95)); }))
            .WithTag(Tags.Analog("ME-EXH-TT02"))
            .WithChannel(2, SignalType.AI, "ME-EXH-TT01");

        var result = ChangeSetEvaluator.Evaluate(
            [new DeleteTagOp { TagName = "ME-EXH-TT01" }, new BindChannelOp { TagName = "ME-EXH-TT02", New = 2 }, new DeleteTagOp { TagName = "ME-EXH-TT01" }],
            project);

        result.Violations.Should().Contain(v => v.Code == ViolationCodes.DeleteTagWithAlarms && v.Severity == Severity.Warning);
        result.Violations.Should().Contain(v => v.Code == ViolationCodes.TagNotFound);
        result.Violations.Should().NotContain(v => v.Code == ViolationCodes.ChannelInUse);
    }

    [Fact]
    public void Safety_classified_tags_require_a_second_approver()
    {
        var onSil = Evaluate(new UpdateTagOp { TagName = "ME-SPD-ST01", Field = "description", Old = "desc", New = "Overspeed" });
        onSil.RequiresSecondApprover.Should().BeTrue();
        onSil.Violations.Should().ContainSingle(v => v.Code == ViolationCodes.RequiresSecondApprover && v.Severity == Severity.Info && v.TagName == "ME-SPD-ST01");
        onSil.HasErrors.Should().BeFalse();

        // two ops on the same tag report it once
        Evaluate(
            new UpdateTagOp { TagName = "ME-SPD-ST01", Field = "description", Old = "desc", New = "a" },
            new UpdateTagOp { TagName = "ME-SPD-ST01", Field = "description", Old = "a", New = "b" })
            .Violations.Count(v => v.Code == ViolationCodes.RequiresSecondApprover).Should().Be(1);

        // downgrading, upgrading, creating and deleting classified tags all count
        Evaluate(new UpdateTagOp { TagName = "ME-SPD-ST01", Field = "safetyClass", Old = "SIL", New = "NONE" }).RequiresSecondApprover.Should().BeTrue();
        Evaluate(new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "safetyClass", Old = "NONE", New = "SIL" }).RequiresSecondApprover.Should().BeTrue();
        Evaluate(Create() with { SafetyClass = SafetyClass.ESD }).RequiresSecondApprover.Should().BeTrue();
        Evaluate(new DeleteTagOp { TagName = "ME-SPD-ST01" }).RequiresSecondApprover.Should().BeTrue();
        Evaluate(Create()).RequiresSecondApprover.Should().BeFalse();
    }

    [Fact]
    public void Evaluation_only_reads_the_project()
    {
        var project = Project();
        var before = project.GetTag("ME-EXH-TT01");
        ChangeSetEvaluator.Evaluate([new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "unit", Old = "°C", New = "bar" }], project);
        project.GetTag("ME-EXH-TT01").Should().BeSameAs(before);
    }
}

public class OldValueResolverTests
{
    private static readonly TagSnapshot Current = Tags.Analog("ME-EXH-TT01", b =>
    {
        b.Group = "G";
        b.Channel = 7;
        b.Limits = Tags.Limits((AlarmLevel.H, 95));
    });

    [Fact]
    public void Fills_old_for_each_op_type()
    {
        ((UpdateTagOp)OldValueResolver.Fill(new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "unit", New = "bar" }, Current)).Old.Should().Be("°C");
        ((SetAlarmLimitOp)OldValueResolver.Fill(new SetAlarmLimitOp { TagName = "ME-EXH-TT01", Level = AlarmLevel.H, New = null }, Current)).Old.Should().Be(Current.Limits[AlarmLevel.H]);
        ((SetAlarmLimitOp)OldValueResolver.Fill(new SetAlarmLimitOp { TagName = "ME-EXH-TT01", Level = AlarmLevel.L, New = null }, Current)).Old.Should().BeNull();
        ((AssignGroupOp)OldValueResolver.Fill(new AssignGroupOp { TagName = "ME-EXH-TT01", New = "X" }, Current)).Old.Should().Be("G");
        ((BindChannelOp)OldValueResolver.Fill(new BindChannelOp { TagName = "ME-EXH-TT01", New = 1 }, Current)).Old.Should().Be(7);
        ((DeleteTagOp)OldValueResolver.Fill(new DeleteTagOp { TagName = "ME-EXH-TT01" }, Current)).Old.Should().Be(Current);
    }

    [Fact]
    public void Leaves_other_ops_untouched()
    {
        var create = new CreateTagOp { TagName = "ME-EXH-TT09", SignalType = SignalType.AI };
        OldValueResolver.Fill(create, Current).Should().BeSameAs(create);

        var unknownField = new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "nope", New = "x" };
        OldValueResolver.Fill(unknownField, Current).Should().BeSameAs(unknownField);

        var missing = new DeleteTagOp { TagName = "ME-EXH-TT01" };
        OldValueResolver.Fill(missing, null).Should().BeSameAs(missing);
    }
}

public class SimulatorTests
{
    [Fact]
    public void Current_reads_through_to_the_project_until_touched()
    {
        var project = new FakeProject().WithTag(Tags.Analog("ME-EXH-TT01"));
        var sim = new ChangeSetSimulator(project);

        sim.Current("ME-EXH-TT01").Should().BeSameAs(project.GetTag("ME-EXH-TT01"));
        sim.Current("NOPE").Should().BeNull();
        sim.Apply(new UpdateTagOp { TagName = "ME-EXH-TT01", Field = "unit", Old = "°C", New = "bar" });
        sim.Current("ME-EXH-TT01")!.Unit.Should().Be("bar");
        sim.Touched.Should().Equal("ME-EXH-TT01");
    }
}

public class TagFieldsTests
{
    [Theory]
    [InlineData("rangeLow", "5", "5.0", true)]
    [InlineData("rangeLow", "5", "6", false)]
    [InlineData("rangeLow", "x", "x", true)]
    [InlineData("rangeLow", null, "", true)]
    [InlineData("rangeLow", " ", null, true)]
    [InlineData("safetyClass", "sil", "SIL", true)]
    [InlineData("description", "a", "A", false)]
    [InlineData("description", " a ", "a", true)]
    public void SameValue(string field, string? a, string? b, bool expected) =>
        TagFields.SameValue(field, a, b).Should().Be(expected);

    [Fact]
    public void Get_and_names()
    {
        var tag = Tags.Analog(tweak: b => { b.Low = null; });
        TagFields.Get(tag, "rangeLow").Should().BeNull();
        TagFields.Get(tag, "rangeHigh").Should().Be("500");
        TagFields.Get(tag, "signalType").Should().Be("AI");
        TagFields.Get(tag, "safetyClass").Should().Be("NONE");
        TagFields.Names.Should().HaveCount(6).And.OnlyContain(n => TagFields.IsKnown(n));
        TagFields.IsKnown("tagName").Should().BeFalse();
    }

    [Fact]
    public void TrySet_returns_null_for_invalid_values()
    {
        var tag = Tags.Analog();
        TagFields.TrySet(tag, "rangeLow", "zzz").Should().BeNull();
        TagFields.TrySet(tag, "rangeLow", "10")!.RangeLow.Should().Be(10);
        TagFields.TrySet(tag, "unit", "  ")!.Unit.Should().BeNull();
    }
}
