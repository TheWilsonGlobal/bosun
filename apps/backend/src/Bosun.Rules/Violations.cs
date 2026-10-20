using Bosun.Domain;

namespace Bosun.Rules;

public enum Severity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>A structured rule result. <c>Code</c> is stable and machine-readable; the UI maps it.</summary>
public sealed record RuleViolation(string Code, Severity Severity, string? TagName, string? Field, string Message);

/// <summary>Stable violation codes.</summary>
public static class ViolationCodes
{
    // Change-set level
    public const string EmptyChangeSet = "EMPTY_CHANGE_SET";
    public const string TooManyOps = "TOO_MANY_OPS";
    public const string RequiresSecondApprover = "REQUIRES_SECOND_APPROVER";

    // Op level
    public const string DuplicateInChangeSet = "DUPLICATE_IN_CHANGESET";
    public const string DuplicateInProject = "DUPLICATE_IN_PROJECT";
    public const string TagNotFound = "TAG_NOT_FOUND";
    public const string OldValueMismatch = "OLD_VALUE_MISMATCH";
    public const string UnknownField = "UNKNOWN_FIELD";
    public const string InvalidValue = "INVALID_VALUE";
    public const string AlarmGroupUnknown = "ALARM_GROUP_UNKNOWN";
    public const string ChannelUnknown = "CHANNEL_UNKNOWN";
    public const string ChannelSignalMismatch = "CHANNEL_SIGNAL_MISMATCH";
    public const string ChannelInUse = "CHANNEL_IN_USE";
    public const string DeleteTagWithAlarms = "DELETE_TAG_WITH_ALARMS";

    // Tag level
    public const string TagNameConvention = "TAG_NAME_CONVENTION";
    public const string UnknownSystem = "UNKNOWN_SYSTEM";
    public const string RangeMissing = "RANGE_MISSING";
    public const string RangeInvalid = "RANGE_INVALID";
    public const string RangeOnDiscrete = "RANGE_ON_DISCRETE";
    public const string UnitMissing = "UNIT_MISSING";
    public const string UnitUnknown = "UNIT_UNKNOWN";
    public const string UnitNotAllowed = "UNIT_NOT_ALLOWED";
    public const string LimitsOnDiscrete = "LIMITS_ON_DISCRETE";
    public const string LimitOutOfRange = "LIMIT_OUT_OF_RANGE";
    public const string LimitOrder = "LIMIT_ORDER";
    public const string DelayInvalid = "DELAY_INVALID";
    public const string HysteresisInvalid = "HYSTERESIS_INVALID";
    public const string HysteresisLarge = "HYSTERESIS_LARGE";
}

/// <summary>Read-only access to the persisted project that the rules compare against.</summary>
public interface IProjectView
{
    TagSnapshot? GetTag(string tagName);
    bool AlarmGroupExists(string name);
    ChannelInfo? GetChannel(int channelId);
}

/// <summary>A channel and the tag (if any) it is currently bound to.</summary>
public sealed record ChannelInfo(int Id, SignalType SignalType, string? BoundTagName);

public sealed record RuleOptions
{
    /// <summary>The ship systems the demo project (and its generator) uses.</summary>
    public static IReadOnlySet<string> DefaultSystems { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "ME", "AE", "BLR", "PMS", "BWTS", "TANK" };

    // Declared after DefaultSystems: static initialisers run in textual order.
    public static RuleOptions Default { get; } = new();

    /// <summary>Plan s7.3: at most 200 ops per change set.</summary>
    public int MaxOps { get; init; } = 200;

    /// <summary>
    /// A tag whose SYSTEM segment is not in this set gets an UNKNOWN_SYSTEM warning (never an error: a new system is
    /// legitimate, it just deserves a second look). Null disables the check.
    /// </summary>
    public IReadOnlySet<string>? KnownSystems { get; init; } = DefaultSystems;
}
