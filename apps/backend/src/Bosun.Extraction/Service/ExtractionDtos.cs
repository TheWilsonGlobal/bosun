using Bosun.Domain.Entities;

namespace Bosun.Extraction;

// REST contract of the extraction review (JSON: camelCase, enums as camelCase names, like the rest of the API).

public sealed record UploadResultDto(int DocumentId, int ExtractionId);

public sealed record ExtractionSummaryDto(
    int Rows,
    int Auto,
    int Review,
    int MustFix,
    int Accepted,
    int Excluded,
    int HumanCorrected,
    int InjectionFlagged,
    int OpenMustFix);

public sealed record ExtractionBriefDto(int Id, ExtractionStatus Status, DateTime CreatedAt, ExtractionSummaryDto Summary);

public sealed record DocumentDto(int Id, int ProjectId, string DisplayName, long SizeBytes, int PageCount, string UploadedBy, DateTime UploadedAt, ExtractionBriefDto? LatestExtraction);

public sealed record ExtractionProgressDto(string? Phase, int PagesDone, int PagesTotal);

public sealed record ExtractionTokensDto(int Prompt, int Candidate, int Thought, int Total, int LlmCalls, double CostUsd);

public sealed record FieldDto(
    object? Value,
    string? Raw,
    double? Confidence,
    string? Band,
    string Source,
    IReadOnlyList<string> Flags,
    IReadOnlyList<RowViolation> Violations);

public sealed record ExtractedRowDto(
    int Id,
    int Seq,
    int Page,
    string SourcePath,
    string? SourceRef,
    string? Snippet,
    double[]? Bbox,
    ExtractedRowState State,
    string Band,
    double Score,
    bool HumanCorrected,
    IReadOnlyList<string> Flags,
    IReadOnlyList<RowViolation> Violations,
    IReadOnlyDictionary<string, FieldDto> Fields,
    int? ChangeSetId,
    int Version);

public sealed record ExtractionDto(
    int Id,
    int ProjectId,
    int DocumentId,
    DocumentDto Document,
    ExtractionStatus Status,
    ExtractionProgressDto Progress,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? Model,
    string? PromptVersion,
    string? ErrorCode,
    string? ErrorMessage,
    ExtractionTokensDto Tokens,
    IReadOnlyList<PageReport> Pages,
    ExtractionSummaryDto Summary,
    IReadOnlyList<ExtractedRowDto> Rows,
    IReadOnlyList<int> ChangeSetIds,
    BandThresholdsDto Bands);

/// <summary>The thresholds the bands follow, so the review grid never hard-codes them.</summary>
public sealed record BandThresholdsDto(double AutoAtLeast, double ReviewAtLeast)
{
    public static BandThresholdsDto Current { get; } = new(Bands.AutoThreshold, Bands.ReviewThreshold);
}

public sealed record PatchRowBody(Dictionary<string, System.Text.Json.JsonElement>? Fields, bool? Excluded);

public sealed record AcceptBody(List<int>? RowIds);

public sealed record AcceptedChangeSetDto(int Id, string Title, int OpCount, IReadOnlyList<int> RowIds);

public sealed record AcceptResultDto(int ExtractionId, IReadOnlyList<AcceptedChangeSetDto> ChangeSets, IReadOnlyList<int> AcceptedRowIds);

/// <summary>An image of a page (or of one row's region of it).</summary>
public sealed record PageImage(byte[] Png, bool Cropped);
