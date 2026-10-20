using System.Text.RegularExpressions;
using Bosun.Domain;

namespace Bosun.Rules;

/// <summary>Known engineering units per signal class.</summary>
public static class UnitCatalog
{
    private static readonly HashSet<string> AnalogUnits = new(StringComparer.Ordinal)
    {
        "°C", "bar", "mbar", "kPa", "MPa", "%", "mm", "m", "m3", "L", "t", "kg", "kW", "MW", "kVA", "kVAr",
        "A", "V", "kV", "Hz", "rpm", "m3/h", "L/h", "kg/h", "t/h", "mm/s", "ppm", "pH", "mS/cm", "m/s", "kN",
        "kNm", "cSt", "°", "NTU", "mg/L",
    };

    public static bool IsKnownAnalogUnit(string unit) => AnalogUnits.Contains(unit);

    public static IReadOnlyCollection<string> All => AnalogUnits;
}

/// <summary>
/// Deterministic per-tag rules. Pure functions: the same snapshot always yields the same violations.
/// Used for change sets (final state of every touched tag) and later for extracted rows.
/// </summary>
public static class TagRules
{
    // {SYSTEM}-{EQUIP}-{MEASURE}{NN}: e.g. ME-EXH-TT01. Hand-written Regex (not source-generated) so coverage measures our code only.
    private static readonly Regex NameConvention =
        new(@"^(?<sys>[A-Z][A-Z0-9]{1,5})-(?<eq>[A-Z][A-Z0-9]{1,7})-(?<meas>[A-Z]{2,4})(?<nn>[0-9]{2,3})$", RegexOptions.CultureInvariant);

    /// <summary>Splits a conforming name into (system, equipment); null when it does not match the convention.</summary>
    public static (string System, string Equipment)? ParseName(string tagName)
    {
        var match = NameConvention.Match(tagName);
        return match.Success ? (match.Groups["sys"].Value, match.Groups["eq"].Value) : null;
    }

    public static bool IsAnalog(SignalType type) => type is SignalType.AI or SignalType.AO;

    public static IReadOnlyList<RuleViolation> Validate(TagSnapshot tag, RuleOptions? options = null)
    {
        options ??= RuleOptions.Default;
        var found = new List<RuleViolation>();
        var name = tag.TagName;

        void Add(string code, Severity severity, string field, string message) =>
            found.Add(new RuleViolation(code, severity, name, field, message));

        var parsed = ParseName(name);
        if (parsed is null)
        {
            Add(ViolationCodes.TagNameConvention, Severity.Error, "tagName",
                $"'{name}' does not match {{SYSTEM}}-{{EQUIP}}-{{MEASURE}}{{NN}} (e.g. ME-EXH-TT01).");
        }
        else if (options.KnownSystems is { } systems && !systems.Contains(parsed.Value.System))
        {
            Add(ViolationCodes.UnknownSystem, Severity.Warning, "tagName", $"System '{parsed.Value.System}' is not a known system.");
        }

        var analog = IsAnalog(tag.SignalType);
        var rangeValid = false;

        if (analog)
        {
            if (tag.RangeLow is not { } low || tag.RangeHigh is not { } high)
            {
                Add(ViolationCodes.RangeMissing, Severity.Error, "rangeLow", "Analog tags need rangeLow and rangeHigh.");
            }
            else if (!double.IsFinite(low) || !double.IsFinite(high) || low >= high)
            {
                Add(ViolationCodes.RangeInvalid, Severity.Error, "rangeLow", $"rangeLow ({low}) must be less than rangeHigh ({high}).");
            }
            else
            {
                rangeValid = true;
            }

            if (string.IsNullOrEmpty(tag.Unit))
            {
                Add(ViolationCodes.UnitMissing, Severity.Error, "unit", "Analog tags need an engineering unit.");
            }
            else if (!UnitCatalog.IsKnownAnalogUnit(tag.Unit))
            {
                Add(ViolationCodes.UnitUnknown, Severity.Error, "unit", $"Unit '{tag.Unit}' is not a known unit for {tag.SignalType} signals.");
            }
        }
        else
        {
            if (tag.RangeLow is not null || tag.RangeHigh is not null)
            {
                Add(ViolationCodes.RangeOnDiscrete, Severity.Warning, "rangeLow", $"{tag.SignalType} tags do not use a range.");
            }

            if (!string.IsNullOrEmpty(tag.Unit))
            {
                Add(ViolationCodes.UnitNotAllowed, Severity.Error, "unit", $"{tag.SignalType} tags cannot have a unit ('{tag.Unit}').");
            }

            if (tag.Limits.Count > 0)
            {
                Add(ViolationCodes.LimitsOnDiscrete, Severity.Error, "limits", $"{tag.SignalType} tags cannot have analog alarm limits.");
            }

            return found;
        }

        ValidateLimits(tag, rangeValid, Add);
        return found;
    }

    private static void ValidateLimits(TagSnapshot tag, bool rangeValid, Action<string, Severity, string, string> add)
    {
        var low = tag.RangeLow.GetValueOrDefault();
        var high = tag.RangeHigh.GetValueOrDefault();
        var span = high - low;
        AlarmLimitValue? previous = null;
        AlarmLevel previousLevel = default;

        foreach (var (level, limit) in tag.Limits)
        {
            var field = $"limits.{level}";

            if (rangeValid && !(limit.Value >= low && limit.Value <= high))
            {
                add(ViolationCodes.LimitOutOfRange, Severity.Error, field,
                    $"{level} limit {limit.Value} is outside the range {low}..{high}.");
            }

            if (previous is not null && !(previous.Value < limit.Value))
            {
                add(ViolationCodes.LimitOrder, Severity.Error, field,
                    $"{level} ({limit.Value}) must be greater than {previousLevel} ({previous.Value}); required order is LL < L < H < HH.");
            }

            if (!(limit.DelayS >= 0))
            {
                add(ViolationCodes.DelayInvalid, Severity.Error, $"{field}.delayS", $"{level} delay must be >= 0 s.");
            }

            if (!(limit.Hysteresis >= 0) || (rangeValid && limit.Hysteresis >= span))
            {
                add(ViolationCodes.HysteresisInvalid, Severity.Error, $"{field}.hysteresis",
                    $"{level} hysteresis must be >= 0 and smaller than the range span.");
            }
            else if (rangeValid && limit.Hysteresis > span * 0.1)
            {
                add(ViolationCodes.HysteresisLarge, Severity.Warning, $"{field}.hysteresis",
                    $"{level} hysteresis exceeds 10% of the range span.");
            }

            previous = limit;
            previousLevel = level;
        }
    }
}
