using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bosun.Domain;

/// <summary>Shared JSON settings: the API, the ChangeOp column, audit diffs and .bosun.json all use these.</summary>
public static class BosunJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.AllowOutOfOrderMetadataProperties = true;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Configure(options);
        return options;
    }
}
