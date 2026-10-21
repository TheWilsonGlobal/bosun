using Bosun.Application.Rag;

namespace Bosun.Rag.Tests;

public class ChunkerTests
{
    private static string Paragraphs(string prefix, int count, int sentencesPerParagraph = 4) =>
        string.Join("\n\n", Enumerable.Range(1, count).Select(p =>
            string.Join(" ", Enumerable.Range(1, sentencesPerParagraph).Select(s => $"{prefix} paragraph {p} sentence {s} talks about alarm limits, delays and thermocouples in some detail."))));

    [Fact]
    public void Headings_are_hard_boundaries_and_a_small_section_is_one_chunk_with_its_own_section_name()
    {
        var chunks = Chunker.Chunk("doc", "# Title\n\n## First\n\nAlpha text.\n\n## Second\n\nBeta text.\n\n### Third (level 3)\n\nGamma text.\n");

        chunks.Select(c => c.Section).Should().Equal("First", "Second", "Third (level 3)");
        chunks.Select(c => c.Text).Should().Equal("Alpha text.", "Beta text.", "Gamma text.");
        chunks.Select(c => c.Ordinal).Should().Equal(0, 1, 2);
        chunks.Should().OnlyContain(c => c.Source == "doc");
    }

    [Fact]
    public void Text_before_the_first_heading_becomes_an_Introduction_chunk_and_an_empty_section_makes_no_chunk()
    {
        var chunks = Chunker.Chunk("doc", "Loose opening line.\n\n## Empty\n\n## Full\n\nBody.");

        chunks.Select(c => c.Section).Should().Equal("Introduction", "Full");
    }

    [Fact]
    public void A_hash_line_inside_a_code_fence_is_not_a_heading()
    {
        var chunks = Chunker.Chunk("doc", "## Code\n\n```\n# not a heading\nvalue = 1\n```\n\nAfter the fence.");

        chunks.Should().ContainSingle().Which.Text.Should().Contain("# not a heading").And.Contain("After the fence.");
    }

    [Fact]
    public void A_long_section_is_split_into_windows_of_the_target_size_that_never_cross_a_heading()
    {
        var markdown = $"## Short\n\nStays whole.\n\n## Long\n\n{Paragraphs("Long", 30)}\n\n## After\n\nTail.";
        var options = new ChunkerOptions(TargetTokens: 300, MaxTokens: 400);

        var chunks = Chunker.Chunk("doc", markdown, options);

        var longChunks = chunks.Where(c => c.Section == "Long").ToList();
        longChunks.Count.Should().BeGreaterThan(2);
        longChunks.Should().OnlyContain(c => Chunker.EstimateTokens(c.Text) <= options.TargetTokens + 20, "windows stay near the target (a paragraph is never cut mid-way unless it alone is too big)");
        chunks.Where(c => c.Section == "Short").Should().ContainSingle().Which.Text.Should().Be("Stays whole.");
        chunks.Where(c => c.Section == "After").Should().ContainSingle().Which.Text.Should().Be("Tail.");
        chunks.Select(c => c.Section).Should().ContainInOrder("Short", "Long", "After");
        chunks.Select(c => c.Ordinal).Should().Equal(Enumerable.Range(0, chunks.Count));
    }

    [Fact]
    public void Consecutive_windows_overlap_by_about_fifteen_percent()
    {
        var chunks = Chunker.Chunk("doc", $"## Long\n\n{Paragraphs("Overlap", 40, sentencesPerParagraph: 1)}", new ChunkerOptions(TargetTokens: 300, MaxTokens: 400)).ToList();

        chunks.Count.Should().BeGreaterThan(2);
        for (var i = 1; i < chunks.Count; i++)
        {
            var previous = chunks[i - 1].Text.Split("\n\n");
            var current = chunks[i].Text.Split("\n\n");
            var shared = current.TakeWhile((paragraph, n) => n < previous.Length && previous.Contains(paragraph)).ToList();
            shared.Should().NotBeEmpty($"window {i} repeats the end of window {i - 1}");

            var overlapChars = shared.Sum(p => p.Length + 2);
            var fraction = (double)overlapChars / chunks[i - 1].Text.Length;
            fraction.Should().BeInRange(0.10, 0.30, "the carried part is about 15 % of the previous window (paragraph granularity)");
            current[^1].Should().NotBe(previous[^1], "every window moves forward");
        }
    }

    [Fact]
    public void A_single_enormous_paragraph_is_cut_on_sentences_with_overlap_and_still_terminates()
    {
        var sentences = string.Join(" ", Enumerable.Range(1, 400).Select(i => $"Sentence number {i} says something about exhaust gas."));
        var chunks = Chunker.Chunk("doc", $"## Big\n\n{sentences}", new ChunkerOptions(TargetTokens: 200, MaxTokens: 300));

        chunks.Count.Should().BeGreaterThan(5);
        chunks.Should().OnlyContain(c => Chunker.EstimateTokens(c.Text) <= 220);
        chunks[1].Text.Should().StartWith("Sentence number", "the cut is on a sentence boundary");
        string.Join(" ", chunks.Select(c => c.Text)).Should().Contain("Sentence number 400 ", "the end of the text is not lost");
    }

    [Fact]
    public void Pages_come_from_the_character_offset_so_they_are_deterministic_and_never_decrease()
    {
        var markdown = string.Concat(Enumerable.Range(1, 12).Select(i => $"## Section {i}\n\n{new string('x', 900)}\n\n"));

        var first = Chunker.Chunk("doc", markdown, new ChunkerOptions(PageChars: 2000));
        var second = Chunker.Chunk("doc", markdown.Replace("\n", "\r\n", StringComparison.Ordinal), new ChunkerOptions(PageChars: 2000));

        first.Select(c => c.Page).Should().Equal(second.Select(c => c.Page), "CRLF and LF paginate identically");
        first.Select(c => c.Page).Should().BeInAscendingOrder();
        first[0].Page.Should().Be(1);
        first[^1].Page.Should().BeGreaterThan(4);
        Chunker.PageOf(0, 2800).Should().Be(1);
        Chunker.PageOf(2799, 2800).Should().Be(1);
        Chunker.PageOf(2800, 2800).Should().Be(2);
    }

    [Fact]
    public void The_seed_documents_chunk_into_their_15_sections_with_sane_sizes_and_the_long_one_is_split_with_overlap()
    {
        var chunks = Directory.EnumerateFiles(RagHost.SeedDocsDirectory, "*.md").Order()
            .SelectMany(f => Chunker.Chunk(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)))
            .ToList();

        chunks.Select(c => (c.Source, c.Section)).Distinct().Should().HaveCount(15);
        chunks.Select(c => c.Source).Distinct().Should().BeEquivalentTo("alarm-philosophy", "cause-and-effect", "functional-design-spec");
        chunks.Should().OnlyContain(c => Chunker.EstimateTokens(c.Text) <= 800 && c.Text.Length > 100);
        chunks.Should().OnlyContain(c => c.Page >= 1 && c.Page <= 6);
        chunks.Where(c => c.Section == "What this document does not cover").Should().ContainSingle();

        chunks.Should().HaveCount(15, "every seed section fits one chunk (largest is about 700 tokens)");
    }
}

public class RrfTests
{
    [Fact]
    public void Scores_add_up_over_the_lists_a_chunk_appears_in()
    {
        var fused = Rrf.Fuse([[1, 2, 3], [3, 1, 4]], [1.0, 1.0], k: 60);

        fused.Select(f => f.Id).Should().Equal(1, 3, 2, 4);
        fused[0].Score.Should().BeApproximately((1.0 / 61) + (1.0 / 62), 1e-12);
        fused[1].Score.Should().BeApproximately((1.0 / 63) + (1.0 / 61), 1e-12);
        fused.Single(f => f.Id == 2).Score.Should().BeApproximately(1.0 / 62, 1e-12);
    }

    [Fact]
    public void A_chunk_found_by_both_retrievers_beats_one_found_by_a_single_retriever_at_a_better_rank()
    {
        var fused = Rrf.Fuse([[10, 20], [30, 20]], [1.0, 1.0], k: 60);

        fused[0].Id.Should().Be(20);
    }

    [Fact]
    public void Weights_shift_the_balance_and_ties_break_by_first_appearance()
    {
        Rrf.Fuse([[1], [2]], [1.0, 1.0], 60).Select(f => f.Id).Should().Equal(1, 2);
        Rrf.Fuse([[1], [2]], [1.0, 3.0], 60).Select(f => f.Id).Should().Equal(2, 1);
        Rrf.Fuse([[], []], [1.0, 1.0], 60).Should().BeEmpty();
    }

    [Fact]
    public void The_exclusion_marker_or_a_does_not_cover_heading_flags_the_section_and_the_marker_never_reaches_the_text()
    {
        var chunks = Chunker.Chunk("doc", "# T\n\n## Real\n\nAlpha.\n\n## Out of scope\n\n<!-- scope: exclusion -->\n\nNot here: fire.\n\n## What this doc does not cover\n\nBeta.\n");

        chunks.Select(c => c.IsExclusion).Should().Equal(false, true, true);
        chunks[1].Text.Should().Be("Not here: fire.", "the marker line is stripped, so the embedded text and its hash are unchanged");
        chunks.Should().OnlyContain(c => !c.Text.Contains("<!--"));
    }

    [Fact]
    public void A_marker_inside_a_code_fence_is_text_not_a_flag()
    {
        var chunks = Chunker.Chunk("doc", "## Code\n\n```\n<!-- scope: exclusion -->\n```\n");

        chunks.Single().IsExclusion.Should().BeFalse();
    }

    [Fact]
    public void The_seed_alarm_philosophy_marks_exactly_its_scope_section()
    {
        var path = Path.Combine(RagHost.SeedDocsDirectory, "alarm-philosophy.md");
        var chunks = Chunker.Chunk("alarm-philosophy", File.ReadAllText(path));

        chunks.Where(c => c.IsExclusion).Select(c => c.Section).Should().Equal("What this document does not cover");
        File.ReadAllText(path).Should().Contain(Chunker.ExclusionMarker, "the seed uses the explicit marker, not only the heading convention");
    }
}
