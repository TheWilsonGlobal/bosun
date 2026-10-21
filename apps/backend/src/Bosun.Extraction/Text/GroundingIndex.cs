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
    private readonly HashSet<int> boundaries;

    private GroundingIndex(string squashed, HashSet<string> tokens, HashSet<int> boundaries)
    {
        this.squashed = squashed;
        this.tokens = tokens;
        this.boundaries = boundaries;
    }

    private static bool NumberChar(char c) => char.IsDigit(c) || c is '.' or ',';

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
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var all = new StringBuilder();
        var boundaries = new HashSet<int> { 0 };
        foreach (var word in page.Words)
        {
            var squashedWord = Squash(word.Text);
            var start = all.Length;
            all.Append(squashedWord);
            boundaries.Add(start);
            boundaries.Add(all.Length);

            // Inside a word a number may start or end next to a unit sign or a unit ("95°C", "(95)", "95C"), never inside another number or a name ("TT95", "-5").
            for (var i = 1; i < squashedWord.Length; i++)
            {
                var previous = squashedWord[i - 1];
                var current = squashedWord[i];
                var numberEnds = NumberChar(previous) && !NumberChar(current);

                // "0-1200": a dash between two numbers is a range separator, so the second number starts after it; a dash at the start of a
                // word is a minus sign, and the sign is part of the number ("5" is not supported by "-5").
                var afterRangeDash = previous == '-' && i >= 2 && NumberChar(squashedWord[i - 2]);
                var numberStarts = char.IsDigit(current) && (afterRangeDash || !char.IsLetterOrDigit(previous) && previous is not ('.' or ',' or '-' or '+'));
                if (numberEnds || numberStarts)
                {
                    boundaries.Add(start + i);
                }
            }

            tokens.Add(Squash(word.Text));
            foreach (Match part in WordParts().Matches(word.Text))
            {
                tokens.Add(Squash(part.Value));
            }
        }

        return new GroundingIndex(all.ToString(), tokens, boundaries);
    }

    /// <summary>True when the page text supports <paramref name="raw"/> as the value of <paramref name="field"/>.</summary>
    public bool Supports(string field, string raw)
    {
        var needle = Squash(raw);
        if (needle.Length == 0)
        {
            return true;
        }

        if (Fields.IsNumeric(field))
        {
            // A number is supported by a whole number on the page, not by any run of digits inside another one (a limit of 0 or 5).
            for (var at = squashed.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = squashed.IndexOf(needle, at + 1, StringComparison.Ordinal))
            {
                if (boundaries.Contains(at) && boundaries.Contains(at + needle.Length))
                {
                    return true;
                }
            }

            return false;
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
