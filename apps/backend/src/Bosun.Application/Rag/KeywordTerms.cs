using System.Text.RegularExpressions;

namespace Bosun.Application.Rag;

/// <summary>The content words of a question, for the keyword retriever (each store turns them into its own query syntax).</summary>
public static partial class KeywordTerms
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "of", "to", "in", "on", "at", "by", "for", "from", "with", "and", "or", "not",
        "no", "why", "what", "which", "who", "whom", "how", "when", "where", "does", "do", "did", "it", "its", "this", "that", "these", "those",
        "than", "rather", "as", "if", "can", "could", "should", "would", "will", "there", "their", "s", "i", "we", "you", "me", "my", "our",
    };

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    /// <summary>Lower-case words and numbers without stop words, in order, distinct, at most <paramref name="max"/>. Single letters are dropped; single digits are kept ("5 s" vs "10 s").</summary>
    public static IReadOnlyList<string> Extract(string question, int max = 12) =>
        WordPattern().Matches(question.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => !StopWords.Contains(w) && (w.Length > 1 || char.IsDigit(w[0])))
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
}
