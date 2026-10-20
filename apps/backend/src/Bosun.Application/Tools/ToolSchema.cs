using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Bosun.Application.Tools;

/// <summary>
/// JSON conventions for tool arguments and results (snake_case, enums by name). The argument schemas are GENERATED from the
/// C# record types by <see cref="SchemaFor(Type)"/>: the same schema is sent to the model as the function declaration and is
/// what <see cref="JsonSchemaValidator"/> enforces before a tool runs (plan s7.2).
/// </summary>
public static class ToolJson
{
    private static readonly ConcurrentDictionary<Type, JsonNode> Schemas = new();

    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.Strict, // the Web defaults would also accept numbers as strings, and the schema would say so
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    public static JsonNode SchemaFor<T>() => SchemaFor(typeof(T));

    public static JsonNode SchemaFor(Type type) =>
        Schemas.GetOrAdd(type, t => JsonSchemaExporter.GetJsonSchemaAsNode(
            Options,
            t,
            new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true, TransformSchemaNode = Transform }));

    /// <summary>
    /// Adds what the exporter does not read itself: [Description], [Range], [MinLength]/[MaxLength] on the record properties.
    /// "null" is dropped from optional members' types (models omit an optional argument; sending null is tolerated by the validator).
    /// </summary>
    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        if (node is not JsonObject schema)
        {
            return node;
        }

        if (schema["type"] is JsonArray types)
        {
            var kept = types.Select(t => t!.GetValue<string>()).Where(t => t != "null").ToList();
            schema["type"] = kept.Count == 1 ? kept[0] : new JsonArray(kept.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray());
        }

        if (schema["enum"] is JsonArray values)
        {
            var kept = values.Where(v => v is not null).Select(v => v!.DeepClone()).ToArray();
            schema["enum"] = new JsonArray(kept);
            if (schema["type"] is null && kept.All(v => v is JsonValue { } j && j.GetValueKind() == JsonValueKind.String))
            {
                schema["type"] = "string"; // Gemini wants an explicit type next to enum
            }
        }

        schema.Remove("default"); // the exporter writes "default": null for optional members, which says nothing useful

        var attributes = context.PropertyInfo?.AttributeProvider;
        if (attributes is null)
        {
            return schema;
        }

        if (attributes.GetCustomAttributes(typeof(DescriptionAttribute), true).FirstOrDefault() is DescriptionAttribute description)
        {
            schema["description"] = description.Description;
        }

        var isArray = schema["type"]?.ToString() == "array";
        if (attributes.GetCustomAttributes(typeof(RangeAttribute), true).FirstOrDefault() is RangeAttribute range)
        {
            schema["minimum"] = JsonValue.Create(Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture));
            schema["maximum"] = JsonValue.Create(Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture));
        }

        if (attributes.GetCustomAttributes(typeof(MinLengthAttribute), true).FirstOrDefault() is MinLengthAttribute min)
        {
            schema[isArray ? "minItems" : "minLength"] = min.Length;
        }

        if (attributes.GetCustomAttributes(typeof(MaxLengthAttribute), true).FirstOrDefault() is MaxLengthAttribute max)
        {
            schema[isArray ? "maxItems" : "maxLength"] = max.Length;
        }

        return schema;
    }
}

/// <summary>
/// A small JSON Schema validator for the subset <see cref="ToolJson.SchemaFor(Type)"/> produces: type, enum, properties, required,
/// additionalProperties:false, items, minItems/maxItems, minLength/maxLength, minimum/maximum, pattern. Anything else in a
/// schema (for example $ref) is a programming error and throws, so a new construct cannot silently go unchecked.
/// </summary>
public static class JsonSchemaValidator
{
    private static readonly HashSet<string> Supported =
    [
        "type", "enum", "properties", "required", "additionalProperties", "items", "minItems", "maxItems", "minLength", "maxLength",
        "minimum", "maximum", "pattern", "description", "title", "default", "format",
    ];

    public static IReadOnlyList<string> Validate(JsonNode? instance, JsonNode schema)
    {
        var errors = new List<string>();
        Check(instance, schema, "", errors);
        return errors;
    }

    private static void Check(JsonNode? value, JsonNode schemaNode, string path, List<string> errors)
    {
        if (schemaNode is not JsonObject schema)
        {
            return; // `true` accepts anything
        }

        foreach (var key in schema.Select(p => p.Key).Where(k => !Supported.Contains(k)))
        {
            throw new NotSupportedException($"JsonSchemaValidator does not support the '{key}' keyword (at '{path}').");
        }

        var here = path.Length == 0 ? "arguments" : path;
        var kind = KindOf(value);

        if (schema["type"] is { } typeNode)
        {
            var allowed = typeNode is JsonArray array ? array.Select(t => t!.GetValue<string>()).ToList() : [typeNode.GetValue<string>()];
            if (!allowed.Any(t => Matches(t, kind)))
            {
                errors.Add($"{here}: expected {string.Join(" or ", allowed)} but got {kind}.");
                return;
            }
        }

        if (schema["enum"] is JsonArray choices && !choices.Any(c => JsonNode.DeepEquals(c, value)))
        {
            errors.Add($"{here}: must be one of {string.Join(", ", choices.Select(c => c?.ToJsonString()))}.");
            return;
        }

        switch (value)
        {
            case JsonObject obj:
                CheckObject(obj, schema, path, errors);
                break;
            case JsonArray arr:
                CheckArray(arr, schema, path, here, errors);
                break;
            case JsonValue when kind == "string":
                CheckString(value.GetValue<string>(), schema, here, errors);
                break;
            case JsonValue when kind is "number" or "integer":
                CheckNumber(value.GetValue<double>(), schema, here, errors);
                break;
        }
    }

    private static void CheckObject(JsonObject obj, JsonObject schema, string path, List<string> errors)
    {
        var properties = schema["properties"] as JsonObject;
        var required = (schema["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToHashSet() ?? [];
        foreach (var name in required)
        {
            if (!obj.ContainsKey(name) || obj[name] is null)
            {
                errors.Add($"{Join(path, name)}: is required.");
            }
        }

        var closed = schema["additionalProperties"] is JsonValue extra && extra.GetValueKind() == JsonValueKind.False;
        foreach (var (name, child) in obj)
        {
            if (properties is not null && properties[name] is { } childSchema)
            {
                if (child is null && !required.Contains(name))
                {
                    continue; // null for an optional member = omitted
                }

                if (child is not null)
                {
                    Check(child, childSchema, Join(path, name), errors);
                }
            }
            else if (closed)
            {
                var known = properties is null ? "none" : string.Join(", ", properties.Select(p => p.Key));
                errors.Add($"{Join(path, name)}: is not a known argument (known: {known}).");
            }
        }
    }

    private static void CheckArray(JsonArray arr, JsonObject schema, string path, string here, List<string> errors)
    {
        if (schema["minItems"] is { } min && arr.Count < min.GetValue<int>())
        {
            errors.Add($"{here}: needs at least {min.GetValue<int>()} item(s) (has {arr.Count}).");
        }

        if (schema["maxItems"] is { } max && arr.Count > max.GetValue<int>())
        {
            errors.Add($"{here}: allows at most {max.GetValue<int>()} item(s) (has {arr.Count}).");
        }

        if (schema["items"] is { } items)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                Check(arr[i], items, $"{path}[{i}]", errors);
            }
        }
    }

    private static void CheckString(string text, JsonObject schema, string here, List<string> errors)
    {
        if (schema["minLength"] is { } min && text.Length < min.GetValue<int>())
        {
            errors.Add($"{here}: must have at least {min.GetValue<int>()} character(s).");
        }

        if (schema["maxLength"] is { } max && text.Length > max.GetValue<int>())
        {
            errors.Add($"{here}: must have at most {max.GetValue<int>()} characters (has {text.Length}).");
        }

        if (schema["pattern"] is { } pattern && !System.Text.RegularExpressions.Regex.IsMatch(text, pattern.GetValue<string>(), System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            errors.Add($"{here}: does not match the pattern {pattern.GetValue<string>()}.");
        }
    }

    private static void CheckNumber(double number, JsonObject schema, string here, List<string> errors)
    {
        if (schema["minimum"] is { } min && number < min.GetValue<double>())
        {
            errors.Add($"{here}: must be >= {min.GetValue<double>().ToString(CultureInfo.InvariantCulture)}.");
        }

        if (schema["maximum"] is { } max && number > max.GetValue<double>())
        {
            errors.Add($"{here}: must be <= {max.GetValue<double>().ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static string KindOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        _ => node.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => Math.Abs(node.GetValue<double>() % 1) < double.Epsilon ? "integer" : "number",
            _ => "null",
        },
    };

    private static bool Matches(string type, string kind) => type switch
    {
        "number" => kind is "number" or "integer",
        _ => kind == type,
    };
}
