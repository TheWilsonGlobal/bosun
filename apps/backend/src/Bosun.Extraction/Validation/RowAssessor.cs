using System.Collections.Immutable;
using Bosun.Application.Safety;
using Bosun.Domain;
using Bosun.Rules;

namespace Bosun.Extraction;

/// <summary>The numbers behind the confidence step (plan s6.3 step 5). Kept in one place so the evals and the docs can quote them.</summary>
public sealed class ScoreOptions
{
    /// <summary>Base score of a schema-valid, rule-clean field read from the text layer.</summary>
    public double TextBase { get; set; } = 0.95;

    /// <summary>Base score of a field read from a page image (no text layer to ground it in).</summary>
    public double VisionBase { get; set; } = 0.92;

    /// <summary>A value typed by an engineer.</summary>
    public double HumanBase { get; set; } = 1.0;

    public double ErrorPenalty { get; set; } = 0.50;
    public double WarningPenalty { get; set; } = 0.15;
    public double GuessPenalty { get; set; } = 0.10;
    public double UnclearPenalty { get; set; } = 0.25;
    public double NotInSourcePenalty { get; set; } = 0.40;

    /// <summary>Added when the text read and the vision read of the page agree on the field.</summary>
    public double DualAgreeBoost { get; set; } = 0.03;

    /// <summary>When the two reads disagree the field cannot score above this: a person must pick.</summary>
    public double DualDisagreeCap { get; set; } = 0.75;

    public double Ceiling { get; set; } = 0.99;

    /// <summary>A row with instruction-like text (or on such a page) cannot be auto-accepted.</summary>
    public double InjectionRowCap { get; set; } = 0.85;

    public double InjectionDescriptionPenalty { get; set; } = 0.35;
}

/// <summary>What the assessor needs to know about the rest of the world.</summary>
/// <param name="ProjectTags">Tag names that already exist in the project.</param>
/// <param name="SameTag">The other rows of the same document that carry the same tag name (null/empty when unique).</param>
public sealed record AssessContext(IReadOnlySet<string> ProjectTags, IReadOnlyList<RowState> SameTag, ScoreOptions Score)
{
    public static AssessContext Alone(IReadOnlySet<string>? projectTags = null, ScoreOptions? score = null) =>
        new(projectTags ?? ImmutableHashSet<string>.Empty, [], score ?? new ScoreOptions());
}

/// <summary>
/// Plan s6.3 steps 4 and 5 in one deterministic pass over one row: validate with <c>Bosun.Rules</c> (range, limit order, name
/// convention, unit for the signal type), add the extraction-specific checks (duplicates in the document and in the project,
/// unreadable numbers, instruction-like text), then turn evidence + findings into per-field confidence, a row score and a band.
/// Used for the first assessment and again after every human edit.
/// </summary>
public static class RowAssessor
{
    public static void Assess(RowState row, AssessContext context)
    {
        var score = context.Score;
        var violations = new List<RowViolation>();

        // computed flags are recomputed; evidence flags (set by the pipeline) stay
        row.Flags.RemoveAll(f => f is RowFlags.ExactDuplicate or RowFlags.SuspectedInjection);

        ParseProblems(row, violations);
        RuleViolations(row, violations);
        Duplicates(row, context, violations);
        Lengths(row, violations);

        if (row.Values.Where(v => v.Value is string).Any(v => v.Key != Fields.SignalType && InjectionScanner.IsSuspicious((string?)v.Value)))
        {
            row.Flags.Add(RowFlags.SuspectedInjection);
        }

        // ---------- per-field confidence ----------
        foreach (var field in Fields.All)
        {
            var state = row.Field(field);
            state.Violations = violations.Where(v => v.Field == field).ToList();
            state.Flags = FieldFlagsOf(state);
            var hasValue = row.Values.TryGetValue(field, out var value) && value is not null;
            state.Confidence = hasValue || state.Violations.Count > 0 || state.Dual == "disagree" ? FieldScore(row, field, state, hasValue, score) : null;
            state.Band = state.Confidence is { } c ? Bands.Of(c) : null;
        }

        row.Violations = violations;
        var fieldMin = Fields.All.Select(f => row.Fields[f].Confidence).Where(c => c is not null).Select(c => c!.Value).DefaultIfEmpty(0).Min();
        var cap = 1.0;
        if (row.Flags.Contains(RowFlags.SuspectedInjection) || row.Flags.Contains(RowFlags.PageInjectionText) || row.Flags.Contains(RowFlags.DualReadMissing))
        {
            cap = score.InjectionRowCap;
        }

        if (row.Flags.Contains(RowFlags.ProcessingFailed))
        {
            cap = 0;
        }

        row.Score = Math.Round(Math.Min(fieldMin, cap), 4);
        row.Band = Bands.Of(row.Score);
    }

    private static List<string> FieldFlagsOf(FieldState state)
    {
        var flags = new List<string>();
        if (state.Source == "human")
        {
            flags.Add(FieldFlags.HumanEdited);
        }

        if (state.Grounded == false)
        {
            flags.Add(FieldFlags.NotInSource);
        }

        if (state.Guess)
        {
            flags.Add(FieldFlags.NormalisationGuess);
        }

        if (state.Unclear)
        {
            flags.Add(FieldFlags.ModelUnclear);
        }

        if (state.Dual == "agree")
        {
            flags.Add(FieldFlags.DualReadAgree);
        }
        else if (state.Dual == "disagree")
        {
            flags.Add(FieldFlags.DualReadDisagree);
        }

        return flags;
    }

    private static double FieldScore(RowState row, string field, FieldState state, bool hasValue, ScoreOptions o)
    {
        var worstError = state.Violations.Any(v => v.Severity == Severities.Error);
        var anyWarning = state.Violations.Any(v => v.Severity == Severities.Warning);
        if (!hasValue)
        {
            // Nothing was read, and a rule says something should be there: this is what the human must fix.
            return worstError ? 0.30 : state.Dual == "disagree" ? 0.70 : 0.80;
        }

        var value = state.Source switch { "human" => o.HumanBase, "vision" => o.VisionBase, _ => o.TextBase };
        if (state.Grounded == false)
        {
            value -= o.NotInSourcePenalty;
        }

        if (state.Guess)
        {
            value -= o.GuessPenalty;
        }

        if (state.Unclear)
        {
            value -= o.UnclearPenalty;
        }

        if (worstError)
        {
            value -= o.ErrorPenalty;
        }
        else if (anyWarning)
        {
            value -= o.WarningPenalty;
        }

        if (state.Dual == "agree" && !worstError)
        {
            value += o.DualAgreeBoost;
        }
        else if (state.Dual == "disagree")
        {
            value = Math.Min(value, o.DualDisagreeCap);
        }

        if (field == Fields.TagName && row.Flags.Contains(RowFlags.TagNotInSource))
        {
            value -= o.NotInSourcePenalty;
        }

        if (field == Fields.Description && row.Flags.Contains(RowFlags.SuspectedInjection))
        {
            value -= o.InjectionDescriptionPenalty;
        }

        return Math.Round(Math.Clamp(value, 0.02, o.Ceiling), 4);
    }

    // ---------- findings ----------

    private static void ParseProblems(RowState row, List<RowViolation> violations)
    {
        foreach (var field in Fields.All)
        {
            if (row.Field(field).ParseProblem is not { } problem)
            {
                continue;
            }

            var code = field == Fields.SignalType ? RowViolationCodes.SignalTypeUnknown : RowViolationCodes.NumberUnparsable;
            violations.Add(new RowViolation(code, Severities.Error, field, problem));
        }
    }

    private static void RuleViolations(RowState row, List<RowViolation> violations)
    {
        var name = row.Text(Fields.TagName) ?? "";
        var typeText = row.Text(Fields.SignalType);
        SignalType? type = typeText is not null && Enum.TryParse<SignalType>(typeText, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

        if (type is null)
        {
            if (row.Field(Fields.SignalType).ParseProblem is null)
            {
                violations.Add(new RowViolation(RowViolationCodes.SignalTypeMissing, Severities.Error, Fields.SignalType, "The signal type (AI, AO, DI or DO) is missing."));
            }

            // Without a type only the name can be judged.
            foreach (var v in TagRules.Validate(new TagSnapshot { TagName = name, SignalType = SignalType.DI }).Where(v => v.Field == "tagName"))
            {
                violations.Add(Map(v, Fields.TagName));
            }

            return;
        }

        var limits = ImmutableSortedDictionary.CreateBuilder<AlarmLevel, AlarmLimitValue>();
        var delay = row.Number(Fields.DelayS) ?? 0;
        foreach (var field in Fields.Limits)
        {
            if (row.Number(field) is { } limit)
            {
                limits[Fields.LevelOf(field)!.Value] = new AlarmLimitValue(limit, delay, 0);
            }
        }

        var snapshot = new TagSnapshot
        {
            TagName = name,
            Description = row.Text(Fields.Description),
            SignalType = type.Value,
            Unit = row.Text(Fields.Unit),
            RangeLow = row.Number(Fields.RangeLow),
            RangeHigh = row.Number(Fields.RangeHigh),
            Limits = limits.ToImmutable(),
        };

        foreach (var v in TagRules.Validate(snapshot))
        {
            foreach (var field in FieldsOf(v, snapshot))
            {
                violations.Add(Map(v, field));
            }
        }
    }

    private static RowViolation Map(RuleViolation v, string field) =>
        new(v.Code, v.Severity switch { Severity.Error => Severities.Error, Severity.Warning => Severities.Warning, _ => Severities.Info }, field, v.Message);

    /// <summary>The extraction fields a rule finding belongs to (a finding about the range belongs to both ends of it).</summary>
    private static IEnumerable<string> FieldsOf(RuleViolation v, TagSnapshot tag)
    {
        switch (v.Field)
        {
            case "tagName":
                return [Fields.TagName];
            case "unit":
                return [Fields.Unit];
            case "rangeLow":
                if (v.Code == ViolationCodes.RangeMissing)
                {
                    var missing = new List<string>();
                    if (tag.RangeLow is null)
                    {
                        missing.Add(Fields.RangeLow);
                    }

                    if (tag.RangeHigh is null)
                    {
                        missing.Add(Fields.RangeHigh);
                    }

                    return missing;
                }

                if (v.Code == ViolationCodes.RangeOnDiscrete)
                {
                    return new[] { Fields.RangeLow, Fields.RangeHigh }.Where(f => f == Fields.RangeLow ? tag.RangeLow is not null : tag.RangeHigh is not null);
                }

                return [Fields.RangeLow, Fields.RangeHigh];
            case "limits":
                return Fields.Limits.Where(f => tag.Limits.ContainsKey(Fields.LevelOf(f)!.Value));
            case { } f when f.StartsWith("limits.", StringComparison.Ordinal):
                var rest = f["limits.".Length..];
                if (rest.EndsWith(".delayS", StringComparison.Ordinal))
                {
                    return [Fields.DelayS];
                }

                if (!Enum.TryParse<AlarmLevel>(rest, out var level))
                {
                    return [];
                }

                var fields = new List<string> { Fields.FieldOfLevel(level) };
                if (v.Code == ViolationCodes.LimitOrder)
                {
                    // "H must be greater than L": either could be the misread one, so both are marked.
                    var below = tag.Limits.Keys.Where(k => k < level).DefaultIfEmpty().Max();
                    if (tag.Limits.Keys.Any(k => k < level))
                    {
                        fields.Add(Fields.FieldOfLevel(below));
                    }
                }

                return fields;
            default:
                return [];
        }
    }

    private static void Duplicates(RowState row, AssessContext context, List<RowViolation> violations)
    {
        var name = row.Text(Fields.TagName);
        if (name is null)
        {
            return;
        }

        if (context.ProjectTags.Contains(name))
        {
            violations.Add(new RowViolation(RowRuleCodes.DuplicateInProject, Severities.Error, Fields.TagName,
                $"A tag named {name} already exists in the project. Rename the row, or exclude it."));
        }

        if (context.SameTag.Count == 0)
        {
            return;
        }

        var everyone = context.SameTag.Append(row).ToList();
        var identical = everyone.All(r => SameValues(r, row));
        var first = everyone.Min(r => r.Seq);
        if (identical)
        {
            if (row.Seq != first)
            {
                row.Flags.Add(RowFlags.ExactDuplicate);
                violations.Add(new RowViolation(RowRuleCodes.DuplicateInDocument, Severities.Warning, Fields.TagName,
                    $"{name} repeats an earlier row with the same values (page {context.SameTag.Where(r => r.Seq < row.Seq).Min(r => r.Page)}). Exclude the repeat."));
            }

            return;
        }

        violations.Add(new RowViolation(RowRuleCodes.DuplicateInDocument, Severities.Error, Fields.TagName,
            $"{name} appears {everyone.Count} times in the document with different values. Keep one row and exclude the others."));
    }

    private static bool SameValues(RowState a, RowState b) =>
        Fields.All.All(f => Equals(a.Values.GetValueOrDefault(f), b.Values.GetValueOrDefault(f)));

    private static void Lengths(RowState row, List<RowViolation> violations)
    {
        if (row.Text(Fields.TagName) is { Length: > 100 })
        {
            violations.Add(new RowViolation(RowViolationCodes.TagNameTooLong, Severities.Error, Fields.TagName, "A tag name is at most 100 characters."));
        }

        if (row.Text(Fields.Description) is { Length: > 500 })
        {
            violations.Add(new RowViolation(RowViolationCodes.DescriptionTooLong, Severities.Error, Fields.Description, "A description is at most 500 characters."));
        }
    }
}

/// <summary>Aliases so the duplicate codes of the row rules read the same as the rule engine ones.</summary>
internal static class RowRuleCodes
{
    public const string DuplicateInDocument = RowViolationCodes.DuplicateInDocument;
    public const string DuplicateInProject = RowViolationCodes.DuplicateInProject;
}
