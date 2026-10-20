using System.Globalization;
using Bosun.Domain;

namespace Bosun.Rules;

/// <summary>
/// The scalar fields <see cref="UpdateTagOp"/> may change, with string get/set so ops stay uniform.
/// Numbers use the invariant culture; an empty string clears an optional field.
/// </summary>
public static class TagFields
{
    private enum Kind
    {
        Text,
        Number,
        Enum,
    }

    private sealed record Def(
        Kind Kind,
        Func<TagSnapshot, string?> Get,
        Func<TagSnapshot, string?, TagSnapshot?> Set);

    private static readonly Dictionary<string, Def> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["description"] = new(Kind.Text, t => t.Description, (t, v) => t with { Description = Blank(v) }),
        ["unit"] = new(Kind.Text, t => t.Unit, (t, v) => t with { Unit = Blank(v) }),
        ["signalType"] = new(
            Kind.Enum,
            t => t.SignalType.ToString().ToUpperInvariant(),
            (t, v) => ParseEnum<SignalType>(v) is { } e ? t with { SignalType = e } : null),
        ["rangeLow"] = new(
            Kind.Number,
            t => Format(t.RangeLow),
            (t, v) => TryNumber(v, out var d) ? t with { RangeLow = d } : null),
        ["rangeHigh"] = new(
            Kind.Number,
            t => Format(t.RangeHigh),
            (t, v) => TryNumber(v, out var d) ? t with { RangeHigh = d } : null),
        ["safetyClass"] = new(
            Kind.Enum,
            t => t.SafetyClass.ToString().ToUpperInvariant(),
            (t, v) => ParseEnum<SafetyClass>(v) is { } e ? t with { SafetyClass = e } : null),
    };

    public static IReadOnlyCollection<string> Names { get; } = ["description", "unit", "signalType", "rangeLow", "rangeHigh", "safetyClass"];

    public static bool IsKnown(string field) => Fields.ContainsKey(field);

    /// <summary>Current value of a (known) field as the string an op would carry.</summary>
    public static string? Get(TagSnapshot tag, string field) => Fields[field].Get(tag);

    /// <summary>Returns the updated snapshot, or null when <paramref name="value"/> is not valid for the (known) field.</summary>
    public static TagSnapshot? TrySet(TagSnapshot tag, string field, string? value) => Fields[field].Set(tag, value);

    /// <summary>Compares two op values for a field in canonical form (so "95" equals "95.0", "ai" equals "AI", null equals "").</summary>
    public static bool SameValue(string field, string? a, string? b) => Canonical(field, a) == Canonical(field, b);

    private static string Canonical(string field, string? value)
    {
        var blank = Blank(value);
        if (blank is null)
        {
            return "";
        }

        return Fields[field].Kind switch
        {
            Kind.Number => double.TryParse(blank, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d.ToString("R", CultureInfo.InvariantCulture)
                : blank,
            Kind.Enum => blank.ToUpperInvariant(),
            _ => blank,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Format(double? value) => value?.ToString("R", CultureInfo.InvariantCulture);

    private static bool TryNumber(string? value, out double? result)
    {
        result = null;
        if (Blank(value) is not { } text)
        {
            return true;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
        {
            return false;
        }

        result = d;
        return true;
    }

    private static T? ParseEnum<T>(string? value)
        where T : struct, Enum =>
        Blank(value) is { } text && Enum.TryParse<T>(text, true, out var e) && Enum.IsDefined(e) ? e : null;
}
