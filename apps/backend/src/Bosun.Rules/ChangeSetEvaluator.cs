using Bosun.Domain;

namespace Bosun.Rules;

/// <summary>Outcome of evaluating a whole change set.</summary>
public sealed record EvaluationResult(
    IReadOnlyList<RuleViolation> Violations,
    IReadOnlyDictionary<string, TagSnapshot?> FinalState,
    IReadOnlyList<string> TouchedTags,
    bool RequiresSecondApprover)
{
    public bool HasErrors => Violations.Any(v => v.Severity == Severity.Error);

    /// <summary>Violations that stale old values caused (the API maps these to 409 at apply time).</summary>
    public IReadOnlyList<RuleViolation> Conflicts => Violations.Where(v => v.Code == ViolationCodes.OldValueMismatch).ToList();
}

/// <summary>
/// Deterministic validation of a change set against the persisted project: size limit, op-level checks
/// (via <see cref="ChangeSetSimulator"/>), then the per-tag rules on the final state of every touched tag.
/// </summary>
public static class ChangeSetEvaluator
{
    private static bool IsSafetyClassified(TagSnapshot? tag) => tag is not null && tag.SafetyClass.RequiresSecondApprover();

    public static EvaluationResult Evaluate(IReadOnlyList<ChangeOp> ops, IProjectView view, RuleOptions? options = null)
    {
        options ??= RuleOptions.Default;
        var violations = new List<RuleViolation>();

        if (ops.Count == 0)
        {
            violations.Add(new RuleViolation(ViolationCodes.EmptyChangeSet, Severity.Error, null, null, "A change set needs at least one operation."));
        }

        if (ops.Count > options.MaxOps)
        {
            violations.Add(new RuleViolation(ViolationCodes.TooManyOps, Severity.Error, null, null,
                $"A change set may contain at most {options.MaxOps} operations (has {ops.Count})."));
        }

        var simulator = new ChangeSetSimulator(view);
        var secondApproverTags = new List<string>();

        foreach (var op in ops)
        {
            var before = simulator.Current(op.TagName);
            violations.AddRange(simulator.Apply(op));
            var after = simulator.Current(op.TagName);

            if ((IsSafetyClassified(before) || IsSafetyClassified(after)) && !secondApproverTags.Contains(op.TagName))
            {
                secondApproverTags.Add(op.TagName);
            }
        }

        foreach (var name in simulator.Touched)
        {
            if (simulator.FinalState[name] is { } final)
            {
                violations.AddRange(TagRules.Validate(final, options));
            }
        }

        foreach (var name in secondApproverTags)
        {
            violations.Add(new RuleViolation(ViolationCodes.RequiresSecondApprover, Severity.Info, name, "safetyClass",
                $"'{name}' is safety-classified (SIL/ESD): a second approver is required."));
        }

        return new EvaluationResult(violations, simulator.FinalState, simulator.Touched, secondApproverTags.Count > 0);
    }
}

/// <summary>Fills in the <c>Old</c> value of ops that omitted it, from the state the preceding ops leave behind.</summary>
public static class OldValueResolver
{
    /// <summary>Returns the op with <c>Old</c> taken from <paramref name="current"/>; unchanged when the tag does not exist.</summary>
    public static ChangeOp Fill(ChangeOp op, TagSnapshot? current)
    {
        if (current is null)
        {
            return op;
        }

        return op switch
        {
            UpdateTagOp update when TagFields.IsKnown(update.Field) => update with { Old = TagFields.Get(current, update.Field) },
            SetAlarmLimitOp limit => limit with { Old = current.Limits.GetValueOrDefault(limit.Level) },
            AssignGroupOp assign => assign with { Old = current.AlarmGroup },
            BindChannelOp bind => bind with { Old = current.ChannelId },
            DeleteTagOp delete => delete with { Old = current },
            _ => op,
        };
    }
}
