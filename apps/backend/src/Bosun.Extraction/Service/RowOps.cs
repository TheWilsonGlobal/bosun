using Bosun.Domain;

namespace Bosun.Extraction;

/// <summary>
/// An accepted row as change-set operations: one createTag, plus one setAlarmLimit per limit the row carries (delay from the row,
/// hysteresis 0). Safety class is never set from a document. Applying is not here: the ops only go into a DRAFT change set.
/// </summary>
public static class RowOps
{
    public static IReadOnlyList<ChangeOp> Build(RowState row)
    {
        var name = row.Text(Fields.TagName) ?? throw new InvalidOperationException("A row without a tag name cannot become operations.");
        var type = Enum.Parse<SignalType>(row.Text(Fields.SignalType) ?? throw new InvalidOperationException("A row without a signal type cannot become operations."), ignoreCase: true);
        var ops = new List<ChangeOp>
        {
            new CreateTagOp
            {
                TagName = name,
                Description = row.Text(Fields.Description),
                SignalType = type,
                Unit = row.Text(Fields.Unit),
                RangeLow = row.Number(Fields.RangeLow),
                RangeHigh = row.Number(Fields.RangeHigh),
                SafetyClass = SafetyClass.None,
            },
        };

        var delay = row.Number(Fields.DelayS) ?? 0;
        foreach (var field in Fields.Limits)
        {
            if (row.Number(field) is { } value)
            {
                ops.Add(new SetAlarmLimitOp { TagName = name, Level = Fields.LevelOf(field)!.Value, New = new AlarmLimitValue(value, delay, 0) });
            }
        }

        return ops;
    }
}
