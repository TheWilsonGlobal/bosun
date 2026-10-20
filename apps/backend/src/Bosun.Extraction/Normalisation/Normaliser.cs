using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Bosun.Domain;

namespace Bosun.Extraction;

/// <summary>How a document writes decimals. Decided from the unambiguous numbers of a page or document, never from the ambiguous ones.</summary>
public enum NumberLocale
{
    Unknown,
    DecimalDot,
    DecimalComma,
}

/// <summary>Result of reading one number string. <see cref="Guess"/> = the string was ambiguous and a reading was chosen (confidence penalty).</summary>
public readonly record struct NumberParse(double? Value, bool Guess, bool Failed, string? Problem = null)
{
    public static NumberParse Empty => new(null, false, false);
}

/// <summary>Result of mapping a unit or signal-type string onto the canonical vocabulary.</summary>
public readonly record struct TextNormalisation(string? Value, bool Guess, bool Recognised);

/// <summary>
/// Plan s6.3 step 3. Pure, deterministic and unit-tested: numbers ("1.200,5" vs "1,200.5"), units (°C / degC / C), signal types
/// (AI / DI / 4-20mA) and tag names. A mapping that had to guess is reported, so the confidence step can penalise it.
/// </summary>
public static partial class Normaliser
{
    // ---------- numbers ----------

    private static readonly HashSet<string> EmptyMarkers = new(StringComparer.OrdinalIgnoreCase) { "", "-", "--", "---", "—", "–", "n/a", "na", "none", "nil", "tbd", "tba", "x", "." };

    [GeneratedRegex(@"^(?<num>[+-]?[0-9][0-9.,]*|[+-]?[.,][0-9]+)(?<exp>[eE][+-]?[0-9]+)?(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberShape();

    [GeneratedRegex(@"^[0-9]{1,3}([.,][0-9]{3})+$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupedInteger();

    public static NumberParse ParseNumber(string? raw, NumberLocale locale = NumberLocale.Unknown)
    {
        if (raw is null)
        {
            return NumberParse.Empty;
        }

        var text = CleanNumberText(raw);
        if (EmptyMarkers.Contains(text))
        {
            return NumberParse.Empty;
        }

        var match = NumberShape().Match(text);
        if (!match.Success)
        {
            return Fail($"'{raw.Trim()}' is not a number.");
        }

        // Anything after the number must be a unit-like suffix (95 °C), not more digits or a second number.
        var rest = match.Groups["rest"].Value.Trim();
        if (rest.Length > 0 && (char.IsDigit(rest[0]) || rest.Contains(' ') && rest.Any(char.IsDigit)))
        {
            return Fail($"'{raw.Trim()}' is not a single number.");
        }

        var digits = match.Groups["num"].Value;
        var exponent = match.Groups["exp"].Success ? match.Groups["exp"].Value : "";
        var parsed = ParseDigits(digits, locale);
        if (parsed.Failed)
        {
            return Fail(parsed.Problem ?? $"'{raw.Trim()}' is not a number.");
        }

        var value = parsed.Value!.Value;
        if (exponent.Length > 0)
        {
            value *= Math.Pow(10, int.Parse(exponent[1..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        }

        return double.IsFinite(value) ? new NumberParse(value, parsed.Guess, false) : Fail($"'{raw.Trim()}' is out of range.");
    }

    private static NumberParse Fail(string problem) => new(null, false, true, problem);

    private static string CleanNumberText(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.Trim())
        {
            switch (c)
            {
                case '−' or '–' or '—' when sb.Length == 0:
                    sb.Append('-');
                    break;
                case ' ' or ' ' or ' ' or ' ' or '\'' or '’':
                    // thousands separators: "1 200,5", "1'200.5" (a space before a unit suffix is handled by the caller)
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        // collapse "1 200,5" into "1200,5" but keep "95 °C" apart: a space between digit groups of exactly 3 is a thousands separator
        var text = Regex.Replace(sb.ToString(), @"(?<=\d) (?=\d{3}(?:\D|$))", string.Empty, RegexOptions.CultureInvariant);
        return text.Trim();
    }

    private static NumberParse ParseDigits(string digits, NumberLocale locale)
    {
        var sign = 1.0;
        if (digits.StartsWith('-') || digits.StartsWith('+'))
        {
            sign = digits[0] == '-' ? -1.0 : 1.0;
            digits = digits[1..];
        }

        var commas = digits.Count(c => c == ',');
        var dots = digits.Count(c => c == '.');

        NumberParse Ok(string normalised, bool guess = false) =>
            double.TryParse(normalised, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? new NumberParse(sign * v, guess, false)
                : Fail($"'{digits}' is not a number.");

        if (commas == 0 && dots == 0)
        {
            return Ok(digits);
        }

        if (commas > 0 && dots > 0)
        {
            // Both separators: the LAST one is the decimal separator, the other groups thousands.
            var decimalIsComma = digits.LastIndexOf(',') > digits.LastIndexOf('.');
            var thousands = decimalIsComma ? '.' : ',';
            var decimalSep = decimalIsComma ? ',' : '.';
            var lastDecimal = digits.LastIndexOf(decimalSep);
            var integerPart = digits[..lastDecimal];
            var fraction = digits[(lastDecimal + 1)..];
            if (fraction.Contains(thousands) || fraction.Contains(decimalSep) || !ValidGrouping(integerPart, thousands))
            {
                return Fail($"'{digits}' mixes separators in an invalid way.");
            }

            return Ok(integerPart.Replace(thousands.ToString(), string.Empty, StringComparison.Ordinal) + "." + fraction);
        }

        var sep = commas > 0 ? ',' : '.';
        var count = commas > 0 ? commas : dots;
        var other = sep == ',' ? NumberLocale.DecimalComma : NumberLocale.DecimalDot;

        if (count > 1)
        {
            // "1.200.000" / "1,200,000": only thousands grouping can repeat a separator.
            return ValidGrouping(digits, sep)
                ? Ok(digits.Replace(sep.ToString(), string.Empty, StringComparison.Ordinal))
                : Fail($"'{digits}' repeats a separator without grouping digits in threes.");
        }

        var index = digits.IndexOf(sep);
        var before = digits[..index];
        var after = digits[(index + 1)..];
        if (after.Length == 0)
        {
            return Ok(before); // "12." / "12,"
        }

        if (after.Length != 3 || before.Length is 0 or > 3 || before.StartsWith('0'))
        {
            // 2,5  0,75  12.5  .5  1234,567: not a thousands group, so it is a decimal separator.
            return Ok((before.Length == 0 ? "0" : before) + "." + after);
        }

        // Exactly three digits after a lone separator ("1,200" / "1.200"): thousands or decimals?
        if (locale == other)
        {
            return Ok(before + "." + after); // this document writes decimals with this separator
        }

        if (locale != NumberLocale.Unknown)
        {
            return Ok(before + after); // the document uses the other separator for decimals, so this one groups thousands
        }

        // No evidence: read a comma as thousands (US) and a dot as a decimal point, and say it was a guess.
        return sep == ',' ? Ok(before + after, guess: true) : Ok(before + "." + after, guess: true);
    }

    private static bool ValidGrouping(string integerPart, char separator)
    {
        var groups = integerPart.Split(separator);
        if (groups.Length < 2 || groups[0].Length is 0 or > 3 || groups.Any(g => g.Any(c => !char.IsDigit(c))))
        {
            return groups.Length == 1 && groups[0].All(char.IsDigit);
        }

        return groups.Skip(1).All(g => g.Length == 3);
    }

    /// <summary>
    /// Reads the decimal convention from raw number strings: only unambiguous ones count (both separators present, or a lone
    /// separator followed by 1, 2 or 4+ digits). Needs at least <paramref name="minimum"/> such numbers and a 4:1 majority.
    /// </summary>
    public static NumberLocale DetectLocale(IEnumerable<string?> rawNumbers, int minimum = 3)
    {
        int comma = 0, dot = 0;
        foreach (var raw in rawNumbers)
        {
            if (raw is null)
            {
                continue;
            }

            var text = CleanNumberText(raw);
            var match = NumberShape().Match(text);
            if (!match.Success)
            {
                continue;
            }

            var digits = match.Groups["num"].Value.TrimStart('+', '-');
            var lastComma = digits.LastIndexOf(',');
            var lastDot = digits.LastIndexOf('.');
            if (lastComma >= 0 && lastDot >= 0)
            {
                if (lastComma > lastDot)
                {
                    comma++;
                }
                else
                {
                    dot++;
                }

                continue;
            }

            var index = Math.Max(lastComma, lastDot);
            if (index < 0 || digits.Count(c => c is ',' or '.') != 1)
            {
                continue;
            }

            var after = digits.Length - index - 1;
            if (after is 0 or 3)
            {
                continue;
            }

            if (lastComma >= 0)
            {
                comma++;
            }
            else
            {
                dot++;
            }
        }

        if (comma + dot < minimum)
        {
            return NumberLocale.Unknown;
        }

        if (comma >= 4 * Math.Max(1, dot) || dot == 0 && comma >= minimum)
        {
            return NumberLocale.DecimalComma;
        }

        return dot >= 4 * Math.Max(1, comma) || comma == 0 && dot >= minimum ? NumberLocale.DecimalDot : NumberLocale.Unknown;
    }

    // ---------- units ----------

    private static readonly Dictionary<string, string> UnitSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["°c"] = "°C",
        ["° c"] = "°C",
        ["ºc"] = "°C",
        ["º c"] = "°C",
        ["℃"] = "°C",
        ["degc"] = "°C",
        ["deg c"] = "°C",
        ["deg. c"] = "°C",
        ["degree c"] = "°C",
        ["degrees c"] = "°C",
        ["celsius"] = "°C",
        ["deg celsius"] = "°C",
        ["%"] = "%",
        ["pct"] = "%",
        ["percent"] = "%",
        ["% fs"] = "%",
        ["%fs"] = "%",
        ["bar"] = "bar",
        ["mbar"] = "mbar",
        ["millibar"] = "mbar",
        ["kpa"] = "kPa",
        ["mpa"] = "MPa",
        ["mm"] = "mm",
        ["m"] = "m",
        ["m3"] = "m3",
        ["m³"] = "m3",
        ["m^3"] = "m3",
        ["l"] = "L",
        ["ltr"] = "L",
        ["litre"] = "L",
        ["liter"] = "L",
        ["litres"] = "L",
        ["liters"] = "L",
        ["t"] = "t",
        ["kg"] = "kg",
        ["kw"] = "kW",
        ["mw"] = "MW",
        ["kva"] = "kVA",
        ["kvar"] = "kVAr",
        ["a"] = "A",
        ["amp"] = "A",
        ["amps"] = "A",
        ["v"] = "V",
        ["volt"] = "V",
        ["volts"] = "V",
        ["kv"] = "kV",
        ["hz"] = "Hz",
        ["rpm"] = "rpm",
        ["r/min"] = "rpm",
        ["1/min"] = "rpm",
        ["min-1"] = "rpm",
        ["min⁻¹"] = "rpm",
        ["m3/h"] = "m3/h",
        ["m³/h"] = "m3/h",
        ["m^3/h"] = "m3/h",
        ["m3/hr"] = "m3/h",
        ["m³/hr"] = "m3/h",
        ["l/h"] = "L/h",
        ["ltr/h"] = "L/h",
        ["l/hr"] = "L/h",
        ["kg/h"] = "kg/h",
        ["kg/hr"] = "kg/h",
        ["t/h"] = "t/h",
        ["t/hr"] = "t/h",
        ["mm/s"] = "mm/s",
        ["mm/sec"] = "mm/s",
        ["ppm"] = "ppm",
        ["ph"] = "pH",
        ["ms/cm"] = "mS/cm",
        ["m/s"] = "m/s",
        ["kn"] = "kN",
        ["knm"] = "kNm",
        ["kn·m"] = "kNm",
        ["kn.m"] = "kNm",
        ["cst"] = "cSt",
        ["°"] = "°",
        ["ntu"] = "NTU",
        ["mg/l"] = "mg/L",
    };

    // Readings that drop information (gauge/absolute, ton type, bare "C", bare "deg") are guesses: right nearly always, still worth a look.
    private static readonly Dictionary<string, string> UnitGuesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c"] = "°C",
        ["barg"] = "bar",
        ["bar g"] = "bar",
        ["bar(g)"] = "bar",
        ["bar (g)"] = "bar",
        ["bara"] = "bar",
        ["bar a"] = "bar",
        ["bar(a)"] = "bar",
        ["bar (a)"] = "bar",
        ["cbm/h"] = "m3/h",
        ["ton/h"] = "t/h",
        ["tons/h"] = "t/h",
        ["tonne/h"] = "t/h",
        ["deg"] = "°",
        ["degree"] = "°",
        ["degrees"] = "°",
    };

    public static TextNormalisation NormaliseUnit(string? raw)
    {
        if (raw is null)
        {
            return new(null, false, true);
        }

        var cleaned = Regex.Replace(raw.Trim().Replace(' ', ' '), @"\s+", " ");
        if (EmptyMarkers.Contains(cleaned))
        {
            return new(null, false, true);
        }

        // Literal degree/ordinal variants first: "ºC", "° C", "oC" typed by hand.
        var key = cleaned.Replace("º", "°", StringComparison.Ordinal).Replace("˚", "°", StringComparison.Ordinal);
        if (UnitSynonyms.TryGetValue(key, out var canonical) || UnitSynonyms.TryGetValue(key.Replace(" ", string.Empty, StringComparison.Ordinal), out canonical))
        {
            return new(canonical, false, true);
        }

        if (UnitGuesses.TryGetValue(key, out var guessed))
        {
            return new(guessed, true, true);
        }

        return new(cleaned, false, false);
    }

    // ---------- signal types ----------

    private static readonly Dictionary<string, SignalType> SignalSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ai"] = SignalType.AI,
        ["ao"] = SignalType.AO,
        ["di"] = SignalType.DI,
        ["do"] = SignalType.DO,
        ["analog input"] = SignalType.AI,
        ["analogue input"] = SignalType.AI,
        ["analog in"] = SignalType.AI,
        ["analogue in"] = SignalType.AI,
        ["analog output"] = SignalType.AO,
        ["analogue output"] = SignalType.AO,
        ["analog out"] = SignalType.AO,
        ["analogue out"] = SignalType.AO,
        ["digital input"] = SignalType.DI,
        ["digital in"] = SignalType.DI,
        ["digital output"] = SignalType.DO,
        ["digital out"] = SignalType.DO,
        ["4-20ma"] = SignalType.AI,
        ["4-20 ma"] = SignalType.AI,
        ["4..20ma"] = SignalType.AI,
        ["4..20 ma"] = SignalType.AI,
        ["4–20ma"] = SignalType.AI,
        ["4–20 ma"] = SignalType.AI,
        ["4 - 20 ma"] = SignalType.AI,
        ["0-10v"] = SignalType.AI,
        ["0-10 v"] = SignalType.AI,
        ["0..10v"] = SignalType.AI,
        ["pt100"] = SignalType.AI,
        ["pt1000"] = SignalType.AI,
        ["pt 100"] = SignalType.AI,
        ["rtd"] = SignalType.AI,
        ["tc"] = SignalType.AI,
        ["thermocouple"] = SignalType.AI,
        ["4-20ma out"] = SignalType.AO,
        ["4-20 ma out"] = SignalType.AO,
        ["4-20ma output"] = SignalType.AO,
        ["4..20ma out"] = SignalType.AO,
        ["ain"] = SignalType.AI,
        ["din"] = SignalType.DI,
        ["dout"] = SignalType.DO,
        ["aout"] = SignalType.AO,
    };

    [GeneratedRegex(@"(analog(ue)?|4\s*[-–.]+\s*20|0\s*[-–.]+\s*10\s*v|ma\b).*\b(out(put)?)\b|\bout(put)?\b.*(analog(ue)?|4\s*[-–.]+\s*20)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnalogOutputShape();

    [GeneratedRegex(@"digital.*out|relay|binary.*out|contact.*out|\bout(put)?\b.*(24\s*v|relay|contact)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DigitalOutputShape();

    [GeneratedRegex(@"digital|binary|contact|switch|potential[- ]free|on/off|\bn[co]\b|dry", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DigitalInputShape();

    [GeneratedRegex(@"analog|4\s*[-–.]+\s*20|0\s*[-–.]+\s*10\s*v|\bma\b|pt\s*100|rtd|thermocouple|transmitter", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnalogInputShape();

    public static (SignalType? Type, bool Guess, bool Recognised) NormaliseSignalType(string? raw)
    {
        if (raw is null)
        {
            return (null, false, true);
        }

        var cleaned = Regex.Replace(raw.Trim().Replace(' ', ' '), @"\s+", " ");
        if (EmptyMarkers.Contains(cleaned))
        {
            return (null, false, true);
        }

        if (SignalSynonyms.TryGetValue(cleaned, out var exact))
        {
            return (exact, false, true);
        }

        if (AnalogOutputShape().IsMatch(cleaned))
        {
            return (SignalType.AO, true, true);
        }

        if (DigitalOutputShape().IsMatch(cleaned))
        {
            return (SignalType.DO, true, true);
        }

        if (DigitalInputShape().IsMatch(cleaned))
        {
            return (SignalType.DI, true, true);
        }

        return AnalogInputShape().IsMatch(cleaned) ? (SignalType.AI, true, true) : (null, false, false);
    }

    // ---------- tag names and text ----------

    /// <summary>Dashes unified, whitespace removed. Case is NOT changed: a lower-case tag name is a rule violation for a human to fix.</summary>
    public static string? NormaliseTagName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.Trim())
        {
            if (char.IsWhiteSpace(c) || c is '​' or '‌' or '‍' or '﻿')
            {
                continue;
            }

            sb.Append(c is '‐' or '‑' or '‒' or '–' or '—' or '−' ? '-' : c);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    public static string? NormaliseText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = Regex.Replace(raw.Replace(' ', ' '), @"\s+", " ").Trim();
        return EmptyMarkers.Contains(text) ? null : text;
    }
}
