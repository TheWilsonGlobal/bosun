using System.Text;
using System.Text.RegularExpressions;

namespace Bosun.Extraction;

/// <summary>
/// The text of one page in a form that makes "does the page really say this?" cheap. A text-path value the model returned that the
/// page does not contain is a hallucination or an injected value; the check does not depend on the model behaving, so it is one of
/// the independent defences of plan s7.6 (model output is untrusted).
/// </summary>
public sealed partial class GroundingIndex
{
    private readonly string squashed;
    private readonly HashSet<string> tokens;

    private GroundingIndex(string squashed, HashSet<string> tokens)
    {
        this.squashed = squashed;
        this.tokens = tokens;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordParts();

    /// <summary>Lower case, no whitespace, one kind of dash, plain degree sign: the form in which strings are compared.</summary>
    public static string Squash(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c is '​' or '‌' or '‍' or '﻿')
            {
                continue;
            }

            sb.Append(c switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                'º' or '˚' => '°',
                _ => char.ToLowerInvariant(c),
            });
        }

        return sb.ToString();
    }

    public static GroundingIndex Build(PageContent page)
    {
        var all = string.Concat(page.Words.Select(w => w.Text));
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in page.Words)
        {
            tokens.Add(Squash(word.Text));
            foreach (Match part in WordParts().Matches(word.Text))
            {
                tokens.Add(Squash(part.Value));
            }
        }

        return new GroundingIndex(Squash(all), tokens);
    }

    /// <summary>True when the page text supports <paramref name="raw"/> as the value of <paramref name="field"/>.</summary>
    public bool Supports(string field, string raw)
    {
        var needle = Squash(raw);
        if (needle.Length == 0)
        {
            return true;
        }

        if (field != Fields.Description)
        {
            return squashed.Contains(needle, StringComparison.Ordinal);
        }

        // A description may be re-wrapped or lightly trimmed: most of its words must be on the page.
        var words = WordParts().Matches(raw).Select(m => Squash(m.Value)).Where(w => w.Length >= 3).ToList();
        if (words.Count == 0)
        {
            return squashed.Contains(needle, StringComparison.Ordinal);
        }

        return words.Count(tokens.Contains) >= 0.8 * words.Count;
    }
}
