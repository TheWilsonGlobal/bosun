using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Bosun.Extraction;

/// <summary>Confidence bands (plan s6.3 step 5): at least 0.9 = auto-accept, 0.6 to 0.9 = review, below 0.6 = must-fix.</summary>
public static class Bands
{
    public const string Auto = "auto";
    public const string Review = "review";
    public const string MustFix = "must_fix";

    public const double AutoThreshold = 0.9;
    public const double ReviewThreshold = 0.6;

    public static string Of(double score)
    {
        var rounded = Math.Round(score, 4);
        return rounded >= AutoThreshold ? Auto : rounded >= ReviewThreshold ? Review : MustFix;
    }

    /// <summary>0 = best (auto), 2 = worst (must_fix): for taking the worst band of several.</summary>
    public static int Severity(string band) => band switch { Auto => 0, Review => 1, _ => 2 };
}

public static class Severities
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";
}

/// <summary>Row-level flags. A flag is information for the human; the confidence step turns some of them into a lower score.</summary>
public static class RowFlags
{
    /// <summary>A cell of the row contains instruction-like text (deterministic scanner).</summary>
    public const string SuspectedInjection = "suspected_injection";

    /// <summary>Elsewhere on the same page (a note, a title) there is instruction-like text.</summary>
    public const string PageInjectionText = "page_injection_text";

    /// <summary>The tag name the model returned does not occur in the text of the page: invented or injected.</summary>
    public const string TagNotInSource = "tag_not_in_source";

    /// <summary>Same tag, same values as an earlier row of the document (a repeated line).</summary>
    public const string ExactDuplicate = "exact_duplicate";

    /// <summary>The text read and the vision read of this page disagree about whether this row exists.</summary>
    public const string DualReadMissing = "dual_read_missing";

    /// <summary>Normalising or assessing this row threw: the raw text is kept, the band is must_fix.</summary>
    public const string ProcessingFailed = "processing_failed";
}

public static class FieldFlags
{
    public const string NotInSource = "not_in_source";
    public const string NormalisationGuess = "normalisation_guess";
    public const string ModelUnclear = "model_unclear";
    public const string DualReadAgree = "dual_read_agree";
    public const string DualReadDisagree = "dual_read_disagree";
    public const string HumanEdited = "human_edited";
}

/// <summary>A rule finding for a row. <see cref="Field"/> is one of <see cref="Fields"/> (or null for the whole row).</summary>
public sealed record RowViolation(string Code, string Severity, string? Field, string Message);

public static class RowViolationCodes
{
    public const string DuplicateInDocument = "DUPLICATE_IN_DOCUMENT";
    public const string DuplicateInProject = "DUPLICATE_IN_PROJECT";
    public const string NumberUnparsable = "NUMBER_UNPARSABLE";
    public const string SignalTypeUnknown = "SIGNAL_TYPE_UNKNOWN";
    public const string SignalTypeMissing = "SIGNAL_TYPE_MISSING";
    public const string TagNameTooLong = "TAG_NAME_TOO_LONG";
    public const string DescriptionTooLong = "DESCRIPTION_TOO_LONG";
}

/// <summary>What is known about how one field was obtained, and what was concluded from it.</summary>
public sealed class FieldState
{
    /// <summary>"text" (text layer), "vision" (page image) or "human" (typed in the review).</summary>
    public string Source { get; set; } = "text";

    /// <summary>For text pages: did the string occur in the text of the page? Null when not checked (vision, human, no value).</summary>
    public bool? Grounded { get; set; }

    /// <summary>The normaliser had to guess (an ambiguous number, a bare "C", a gauge/absolute bar).</summary>
    public bool Guess { get; set; }

    /// <summary>The model said it could not read this field with certainty.</summary>
    public bool Unclear { get; set; }

    /// <summary>"agree" or "disagree" when a second read of the page (vision for a text page) was compared; otherwise null.</summary>
    public string? Dual { get; set; }

    /// <summary>The string could not be turned into a number / signal type: the problem to show next to the raw text.</summary>
    public string? ParseProblem { get; set; }

    public double? Confidence { get; set; }
    public string? Band { get; set; }
    public List<string> Flags { get; set; } = [];
    public List<RowViolation> Violations { get; set; } = [];
}

/// <summary>
/// The in-memory form of an extracted row, shared by the pipeline (first assessment) and by the human edit (re-assessment). Values are
/// normalised: strings for text fields, doubles for numbers, a signal-type name, or null.
/// </summary>
public sealed class RowState
{
    [JsonIgnore]
    public int Seq { get; set; }

    [JsonIgnore]
    public int Page { get; set; }

    public Dictionary<string, object?> Values { get; set; } = [];
    public Dictionary<string, string?> Raw { get; set; } = [];
    public Dictionary<string, FieldState> Fields { get; set; } = [];

    /// <summary>Evidence flags (set by the pipeline) plus computed flags (set by the assessor).</summary>
    public List<string> Flags { get; set; } = [];
    public List<RowViolation> Violations { get; set; } = [];
    public double Score { get; set; }
    public string Band { get; set; } = Bands.Review;

    // ---------- accessors ----------

    public string? Text(string field) => Values.TryGetValue(field, out var v) ? v as string : null;

    public double? Number(string field) => Values.TryGetValue(field, out var v) ? v switch { double d => d, JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(), _ => null } : null;

    public FieldState Field(string field)
    {
        if (!Fields.TryGetValue(field, out var state))
        {
            state = new FieldState();
            Fields[field] = state;
        }

        return state;
    }

    // ---------- (de)serialisation of the JSON columns ----------

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ValuesToJson() =>
        new JsonObject(Fields_All().Select(f => KeyValuePair.Create(f, ToNode(Values.GetValueOrDefault(f))))).ToJsonString(Json);

    public string RawToJson() => JsonSerializer.Serialize(Raw, Json);

    public string FieldsToJson() => JsonSerializer.Serialize(Fields, Json);

    public string FlagsToJson() => JsonSerializer.Serialize(Flags, Json);

    public string ViolationsToJson() => JsonSerializer.Serialize(Violations, Json);

    public static RowState FromJson(string values, string raw, string fields, string flags, string violations, double score, string band)
    {
        var state = new RowState
        {
            Raw = JsonSerializer.Deserialize<Dictionary<string, string?>>(raw, Json) ?? [],
            Fields = JsonSerializer.Deserialize<Dictionary<string, FieldState>>(fields, Json) ?? [],
            Flags = JsonSerializer.Deserialize<List<string>>(flags, Json) ?? [],
            Violations = JsonSerializer.Deserialize<List<RowViolation>>(violations, Json) ?? [],
            Score = score,
            Band = band,
        };
        foreach (var (key, element) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(values, Json) ?? [])
        {
            state.Values[key] = element.ValueKind switch
            {
                JsonValueKind.Number => element.GetDouble(),
                JsonValueKind.String => element.GetString(),
                _ => null,
            };
        }

        return state;
    }

    private static IEnumerable<string> Fields_All() => Bosun.Extraction.Fields.All;

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(value.ToString()),
    };
}
