using Bosun.Domain;
using Bosun.Rules;

namespace Bosun.Rules.Tests;

public class TagRulesTests
{
    private static string[] Codes(TagSnapshot tag, RuleOptions? options = null) =>
        TagRules.Validate(tag, options).Select(v => v.Code).ToArray();

    [Fact]
    public void Clean_analog_tag_has_no_violations() =>
        TagRules.Validate(Tags.Analog()).Should().BeEmpty();

    [Theory]
    [InlineData("ME-EXH-TT01", true)]
    [InlineData("BWTS-UV1-FT100", true)]
    [InlineData("ME-EXH-TT1", false)]
    [InlineData("me-exh-tt01", false)]
    [InlineData("ME-EXH-01", false)]
    [InlineData("ME_EXH_TT01", false)]
    [InlineData("M-EXH-TT01", false)]
    [InlineData("ME-EXH-TT1000", false)]
    [InlineData("", false)]
    public void Name_convention(string name, bool ok)
    {
        var violations = TagRules.Validate(Tags.Analog(name));
        violations.Any(v => v.Code == ViolationCodes.TagNameConvention).Should().Be(!ok);
        if (!ok)
        {
            violations.Single(v => v.Code == ViolationCodes.TagNameConvention).Should().Match<RuleViolation>(
                v => v.Severity == Severity.Error && v.TagName == name && v.Field == "tagName");
        }
    }

    [Fact]
    public void ParseName_splits_system_and_equipment()
    {
        TagRules.ParseName("ME-EXH-TT01").Should().Be(("ME", "EXH"));
        TagRules.ParseName("nope").Should().BeNull();
    }

    [Fact]
    public void Unknown_system_is_a_warning_only_when_a_system_list_is_given()
    {
        var options = new RuleOptions { KnownSystems = new HashSet<string> { "AE" } };
        var violations = TagRules.Validate(Tags.Analog("ME-EXH-TT01"), options);
        violations.Should().ContainSingle(v => v.Code == ViolationCodes.UnknownSystem && v.Severity == Severity.Warning);

        Codes(Tags.Analog("AE-DG1-TT01"), options).Should().BeEmpty();
        Codes(Tags.Analog("ME-EXH-TT01")).Should().BeEmpty();
    }

    [Fact]
    public void Range_missing_for_analog()
    {
        Codes(Tags.Analog(tweak: b => b.Low = null)).Should().Contain(ViolationCodes.RangeMissing);
        Codes(Tags.Analog(tweak: b => b.High = null)).Should().Contain(ViolationCodes.RangeMissing);
    }

    [Theory]
    [InlineData(100.0, 100.0)]
    [InlineData(200.0, 100.0)]
    [InlineData(double.NaN, 100.0)]
    [InlineData(0.0, double.PositiveInfinity)]
    public void Range_low_must_be_below_high(double low, double high) =>
        Codes(Tags.Analog(tweak: b => { b.Low = low; b.High = high; })).Should().Contain(ViolationCodes.RangeInvalid);

    [Fact]
    public void Analog_unit_rules()
    {
        Codes(Tags.Analog(tweak: b => b.Unit = null)).Should().Contain(ViolationCodes.UnitMissing);
        Codes(Tags.Analog(tweak: b => b.Unit = "")).Should().Contain(ViolationCodes.UnitMissing);
        Codes(Tags.Analog(tweak: b => b.Unit = "furlongs")).Should().Contain(ViolationCodes.UnitUnknown);
        Codes(Tags.Analog(tweak: b => b.Unit = "bar")).Should().BeEmpty();
        UnitCatalog.All.Should().Contain("°C");
    }

    [Theory]
    [InlineData(SignalType.DI)]
    [InlineData(SignalType.DO)]
    public void Discrete_tags_have_no_unit_range_or_limits(SignalType type)
    {
        var clean = Tags.Analog("ME-RUN-XS01", b => { b.Signal = type; b.Unit = null; b.Low = null; b.High = null; });
        Codes(clean).Should().BeEmpty();

        var dirty = Tags.Analog("ME-RUN-XS01", b =>
        {
            b.Signal = type;
            b.Unit = "bar";
            b.Low = 0;
            b.Limits = Tags.Limits((AlarmLevel.H, 1));
        });
        Codes(dirty).Should().BeEquivalentTo(
            ViolationCodes.RangeOnDiscrete, ViolationCodes.UnitNotAllowed, ViolationCodes.LimitsOnDiscrete);

        TagRules.Validate(dirty).Single(v => v.Code == ViolationCodes.RangeOnDiscrete).Severity.Should().Be(Severity.Warning);
        Codes(Tags.Analog("ME-RUN-XS01", b => { b.Signal = type; b.Unit = null; b.Low = null; b.High = 5; }))
            .Should().Equal(ViolationCodes.RangeOnDiscrete);
    }

    [Fact]
    public void Analog_output_is_analog() =>
        Codes(Tags.Analog(tweak: b => { b.Signal = SignalType.AO; b.Unit = null; })).Should().Equal(ViolationCodes.UnitMissing);

    [Fact]
    public void Limits_ordered_and_inside_range()
    {
        var ok = Tags.Analog(tweak: b => b.Limits = Tags.Limits(
            (AlarmLevel.LL, 10), (AlarmLevel.L, 20), (AlarmLevel.H, 400), (AlarmLevel.HH, 450)));
        Codes(ok).Should().BeEmpty();

        // inclusive bounds are fine
        Codes(Tags.Analog(tweak: b => b.Limits = Tags.Limits((AlarmLevel.L, 0), (AlarmLevel.H, 500)))).Should().BeEmpty();
    }

    [Fact]
    public void Limit_below_or_above_range()
    {
        var below = TagRules.Validate(Tags.Analog(tweak: b => b.Limits = Tags.Limits((AlarmLevel.H, -1))));
        below.Should().ContainSingle(v => v.Code == ViolationCodes.LimitOutOfRange && v.Field == "limits.H");

        var above = TagRules.Validate(Tags.Analog(tweak: b => b.Limits = Tags.Limits((AlarmLevel.HH, 501))));
        above.Should().ContainSingle(v => v.Code == ViolationCodes.LimitOutOfRange && v.Field == "limits.HH");

        Codes(Tags.Analog(tweak: b => b.Limits = Tags.Limits((AlarmLevel.H, double.NaN))))
            .Should().Contain(ViolationCodes.LimitOutOfRange);
    }

    [Fact]
    public void Range_check_is_skipped_when_range_itself_is_invalid()
    {
        var codes = Codes(Tags.Analog(tweak: b => { b.Low = 10; b.High = 5; b.Limits = Tags.Limits((AlarmLevel.H, 1000)); }));
        codes.Should().Equal(ViolationCodes.RangeInvalid);
    }

    [Fact]
    public void Limits_must_be_strictly_ascending()
    {
        var equal = TagRules.Validate(Tags.Analog(tweak: b => b.Limits = Tags.Limits((AlarmLevel.H, 90), (AlarmLevel.HH, 90))));
        equal.Should().ContainSingle(v => v.Code == ViolationCodes.LimitOrder && v.Field == "limits.HH");

        var inverted = Codes(Tags.Analog(tweak: b => b.Limits = Tags.Limits((AlarmLevel.L, 90), (AlarmLevel.H, 10))));
        inverted.Should().Equal(ViolationCodes.LimitOrder);

        // order is judged over the levels that are present, so a lone H below a lone L is still wrong
        Codes(Tags.Analog(tweak: b => b.Limits = Tags.Limits((AlarmLevel.LL, 50), (AlarmLevel.HH, 60)))).Should().BeEmpty();
    }

    [Fact]
    public void Delay_must_be_non_negative()
    {
        TagSnapshot With(double delay) => Tags.Analog(tweak: b =>
            b.Limits = Tags.Limits((AlarmLevel.H, 90)).SetItem(AlarmLevel.H, new AlarmLimitValue(90, delay, 1)));

        Codes(With(0)).Should().BeEmpty();
        Codes(With(10)).Should().BeEmpty();
        TagRules.Validate(With(-1)).Should().ContainSingle(v => v.Code == ViolationCodes.DelayInvalid && v.Field == "limits.H.delayS");
        Codes(With(double.NaN)).Should().Contain(ViolationCodes.DelayInvalid);
    }

    [Fact]
    public void Hysteresis_must_be_sane()
    {
        TagSnapshot With(double hysteresis, Action<TagSnapshotBuilder>? tweak = null) => Tags.Analog(tweak: b =>
        {
            tweak?.Invoke(b);
            b.Limits = Tags.Limits((AlarmLevel.H, 90)).SetItem(AlarmLevel.H, new AlarmLimitValue(90, 0, hysteresis));
        });

        Codes(With(0)).Should().BeEmpty();
        Codes(With(50)).Should().BeEmpty(); // exactly 10% of span: not large
        Codes(With(-1)).Should().Equal(ViolationCodes.HysteresisInvalid);
        Codes(With(double.NaN)).Should().Equal(ViolationCodes.HysteresisInvalid);
        Codes(With(500)).Should().Equal(ViolationCodes.HysteresisInvalid); // >= span
        TagRules.Validate(With(51)).Should().ContainSingle(v => v.Code == ViolationCodes.HysteresisLarge && v.Severity == Severity.Warning);

        // with an invalid range there is no span to compare against: only the sign is checked
        Codes(With(1000, b => { b.Low = 5; b.High = 5; })).Should().Equal(ViolationCodes.RangeInvalid);
        Codes(With(-1, b => { b.Low = 5; b.High = 5; })).Should().BeEquivalentTo(ViolationCodes.RangeInvalid, ViolationCodes.HysteresisInvalid);
    }

    [Fact]
    public void Validation_is_deterministic()
    {
        var tag = Tags.Analog("bad", b => { b.Unit = "x"; b.Limits = Tags.Limits((AlarmLevel.H, 9999)); });
        TagRules.Validate(tag).Should().Equal(TagRules.Validate(tag));
    }
}
