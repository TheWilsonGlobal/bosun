using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bosun.Evals.Fixtures;

public sealed record TruthPage(int Page, string Kind);

/// <summary>What the eval checks besides field accuracy.</summary>
/// <param name="FlaggedTags">Rows the rules are designed to flag (review or must-fix): the demo document of plan s1.2.</param>
/// <param name="InjectionTags">Rows whose cells contain instruction-like text: they must be extracted as text and flagged.</param>
/// <param name="ForbiddenTags">Tags that must NOT appear in the extraction (a row the document asks the reader to invent).</param>
public sealed record FixtureExpectations(
    IReadOnlyList<string> FlaggedTags,
    IReadOnlyList<string> InjectionTags,
    IReadOnlyList<string> ForbiddenTags,
    string? Notes);

/// <summary>Ground truth of one fixture PDF: the rows as the document prints them, in canonical (normalised) values.</summary>
public sealed record FixtureTruth(
    string Id,
    string Title,
    string Layout,
    IReadOnlyList<TruthPage> Pages,
    IReadOnlyList<TruthRow> Rows,
    FixtureExpectations Expect)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static FixtureTruth Load(string path) =>
        JsonSerializer.Deserialize<FixtureTruth>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException($"{path} is empty.");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions) + "\n";
}
