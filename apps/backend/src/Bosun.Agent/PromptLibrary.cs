using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bosun.Agent;

/// <summary>
/// A prompt loaded from <c>Bosun.Agent/prompts/{name}.md</c>. <see cref="Version"/> is
/// "{name}@{first 8 hex of SHA-256 of the normalised text}": it changes whenever the file's content changes, and only then,
/// so llm_calls and the audit trail can tell prompt versions apart (plan s9).
/// </summary>
public sealed partial record Prompt(string Name, string Text, string Hash)
{
    public string Version => $"{Name}@{Hash}";

    /// <summary>Replaces <c>{{key}}</c> placeholders. A placeholder without a value is a bug, not an empty string.</summary>
    public string Render(IReadOnlyDictionary<string, string> values) =>
        PlaceholderPattern().Replace(Text, m =>
            values.TryGetValue(m.Groups["key"].Value, out var value)
                ? value
                : throw new KeyNotFoundException($"Prompt '{Name}' needs a value for '{{{{{m.Groups["key"].Value}}}}}'."));

    [GeneratedRegex(@"\{\{(?<key>[A-Za-z0-9_.-]+)\}\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>Line endings and trailing whitespace are normalised first, so a CRLF checkout hashes like an LF one.</summary>
    public static string HashOf(string text)
    {
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)))[..8];
    }

    /// <summary>One version string for a call that uses several prompts: "a+b@{hash of their hashes}".</summary>
    public static string CombinedVersion(params Prompt[] prompts) =>
        prompts.Length == 1
            ? prompts[0].Version
            : $"{string.Join('+', prompts.Select(p => p.Name))}@{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(prompts.Select(p => p.Hash)))))[..8]}";
}

/// <summary>All prompt files, embedded in the assembly at build time (so they ship with it, and tests read the same bytes).</summary>
public sealed class PromptLibrary
{
    private const string ResourcePrefix = "prompts/";
    private readonly Dictionary<string, Prompt> prompts;

    public PromptLibrary(Assembly? assembly = null)
    {
        assembly ??= typeof(PromptLibrary).Assembly;
        prompts = new Dictionary<string, Prompt>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd().TrimStart('﻿');
            var name = resource[ResourcePrefix.Length..^".md".Length];
            prompts[name] = new Prompt(name, text, Prompt.HashOf(text));
        }
    }

    public IReadOnlyCollection<Prompt> All => prompts.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

    public Prompt Get(string name) =>
        prompts.TryGetValue(name, out var prompt)
            ? prompt
            : throw new KeyNotFoundException($"No prompt named '{name}'. Known prompts: {string.Join(", ", prompts.Keys.Order())}.");
}
