using System.Text;
using System.Text.RegularExpressions;

namespace Bosun.Application.Rag;

/// <summary>One searchable slice of a document, with the fields a citation needs.</summary>
public sealed record DocumentChunk(string Source, string Section, int Page, int Ordinal, string Text);

/// <param name="TargetTokens">Window size when a section has to be split.</param>
/// <param name="MaxTokens">A section up to this size stays ONE chunk (headings are hard boundaries, so small sections stay small).</param>
/// <param name="OverlapFraction">Share of the previous window repeated at the start of the next one (15 %).</param>
/// <param name="PageChars">Characters per virtual page; see <see cref="Chunker.PageOf"/>.</param>
public sealed record ChunkerOptions(int TargetTokens = 650, int MaxTokens = 800, double OverlapFraction = 0.15, int PageChars = 2800);

/// <summary>
/// Heading-based Markdown chunking (plan s6.4). Headings of level 1 to 3 are HARD boundaries: a chunk never spans two sections, so
/// a citation names exactly one section. A section of at most <see cref="ChunkerOptions.MaxTokens"/> tokens is a single chunk (this
/// means chunks can be smaller than the 500-token goal; merging neighbours would blur what a citation points at). A longer section
/// is cut into windows of about <see cref="ChunkerOptions.TargetTokens"/> tokens along paragraph and sentence boundaries, each
/// repeating the last ~15 % of the previous window. Tokens are estimated as characters / 4 (no tokenizer dependency).
///
/// Pagination (documented choice): Markdown has no pages, so the text is paginated deterministically at
/// <see cref="ChunkerOptions.PageChars"/> characters of the LF-normalised file; a chunk's page is the page its first character falls on
/// ("p.N" in citations). It is stable as long as the file does not change above that point, and the section stays the real anchor.
/// </summary>
public static partial class Chunker
{
    public const int CharsPerToken = 4;

    public static int EstimateTokens(string text) => (text.Length + CharsPerToken - 1) / CharsPerToken;

    public static int PageOf(int offset, int pageChars) => (Math.Max(0, offset) / Math.Max(1, pageChars)) + 1;

    [GeneratedRegex(@"^(?<hashes>#{1,3})[ \t]+(?<title>.+?)[ \t]*#*[ \t]*$")]
    private static partial Regex HeadingPattern();

    private sealed record Unit(string Text, int Offset, bool StartsParagraph);

    private sealed class Section(string title)
    {
        public string Title { get; } = title;

        public List<(string Text, int Offset)> Paragraphs { get; } = [];
    }

    public static IReadOnlyList<DocumentChunk> Chunk(string source, string markdown, ChunkerOptions? options = null)
    {
        options ??= new ChunkerOptions();
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimStart('﻿');

        var sections = ParseSections(text);
        var chunks = new List<DocumentChunk>();
        foreach (var section in sections.Where(s => s.Paragraphs.Count > 0))
        {
            var body = string.Join("\n\n", section.Paragraphs.Select(p => p.Text));
            if (EstimateTokens(body) <= options.MaxTokens)
            {
                chunks.Add(new DocumentChunk(source, section.Title, PageOf(section.Paragraphs[0].Offset, options.PageChars), chunks.Count, body));
                continue;
            }

            foreach (var (windowText, offset) in Windows(section, options))
            {
                chunks.Add(new DocumentChunk(source, section.Title, PageOf(offset, options.PageChars), chunks.Count, windowText));
            }
        }

        return chunks;
    }

    private static List<Section> ParseSections(string text)
    {
        var sections = new List<Section>();
        Section? current = null;
        var paragraph = new StringBuilder();
        var paragraphOffset = 0;
        var inFence = false;

        void FlushParagraph()
        {
            if (paragraph.Length > 0)
            {
                current?.Paragraphs.Add((paragraph.ToString().TrimEnd(), paragraphOffset));
                paragraph.Clear();
            }
        }

        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            var lineOffset = offset;
            offset += line.Length + 1;

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            var heading = inFence ? null : HeadingPattern().Match(line);
            if (heading is { Success: true })
            {
                FlushParagraph();
                current = new Section(heading.Groups["title"].Value.Trim());
                sections.Add(current);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line) && !inFence)
            {
                FlushParagraph();
                continue;
            }

            if (paragraph.Length == 0)
            {
                paragraphOffset = lineOffset;
                if (current is null)
                {
                    current = new Section("Introduction");
                    sections.Add(current);
                }
            }
            else
            {
                paragraph.Append('\n');
            }

            paragraph.Append(line.TrimEnd());
        }

        FlushParagraph();
        return sections;
    }

    private static IEnumerable<(string Text, int Offset)> Windows(Section section, ChunkerOptions options)
    {
        var targetChars = options.TargetTokens * CharsPerToken;
        var units = SplitIntoUnits(section, targetChars);

        var start = 0;
        while (start < units.Count)
        {
            var end = start;
            var length = 0;
            while (end < units.Count && (end == start || length + units[end].Text.Length + 2 <= targetChars))
            {
                length += units[end].Text.Length + 2;
                end++;
            }

            yield return (Join(units, start, end), units[start].Offset);
            if (end >= units.Count)
            {
                yield break;
            }

            // Next window: walk back from the end until ~15 % of this window is repeated, but always move forward.
            var overlapChars = (int)(length * options.OverlapFraction);
            var next = end;
            var carried = 0;
            while (next - 1 > start && carried < overlapChars)
            {
                next--;
                carried += units[next].Text.Length + 2;
            }

            start = next;
        }
    }

    private static string Join(List<Unit> units, int start, int end)
    {
        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            if (i > start)
            {
                sb.Append(units[i].StartsParagraph ? "\n\n" : " ");
            }

            sb.Append(units[i].Text);
        }

        return sb.ToString();
    }

    /// <summary>Paragraphs, and sentences for a paragraph longer than a window (hard cuts for a sentence that is).</summary>
    private static List<Unit> SplitIntoUnits(Section section, int targetChars)
    {
        var units = new List<Unit>();
        foreach (var (text, offset) in section.Paragraphs)
        {
            if (text.Length <= targetChars)
            {
                units.Add(new Unit(text, offset, true));
                continue;
            }

            var first = true;
            foreach (var (sentence, sentenceOffset) in Sentences(text, offset))
            {
                for (var cut = 0; cut < sentence.Length; cut += targetChars)
                {
                    var piece = sentence.Substring(cut, Math.Min(targetChars, sentence.Length - cut));
                    units.Add(new Unit(piece, sentenceOffset + cut, first));
                    first = false;
                }
            }
        }

        return units;
    }

    private static IEnumerable<(string Text, int Offset)> Sentences(string paragraph, int offset)
    {
        var start = 0;
        for (var i = 0; i < paragraph.Length; i++)
        {
            if (paragraph[i] is '.' or '!' or '?' && (i + 1 == paragraph.Length || char.IsWhiteSpace(paragraph[i + 1])))
            {
                var sentence = paragraph[start..(i + 1)].Trim();
                if (sentence.Length > 0)
                {
                    yield return (sentence, offset + start);
                }

                start = i + 1;
            }
        }

        if (start < paragraph.Length && paragraph[start..].Trim() is { Length: > 0 } rest)
        {
            yield return (rest, offset + start);
        }
    }
}
