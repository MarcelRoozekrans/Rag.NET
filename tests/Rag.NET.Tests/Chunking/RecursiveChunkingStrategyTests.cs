using System.Text;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class RecursiveChunkingStrategyTests
{
    private readonly RecursiveChunkingStrategy _sut = new();

    private static DocumentSection CreateSection(string text) => new()
    {
        Text = text,
        DocumentId = new DocumentId("doc-1"),
        SectionIndex = 0
    };

    [Fact]
    public async Task ChunkAsync_SplitsByParagraphsFirst()
    {
        var text = "First paragraph.\n\nSecond paragraph.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("First paragraph.", chunks[0].Text);
        Assert.Equal("Second paragraph.", chunks[1].Text);
    }

    [Fact]
    public async Task ChunkAsync_FallsBackToSentences_WhenParagraphTooLong()
    {
        var text = "First sentence. Second sentence. Third sentence.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 20));
    }

    [Fact]
    public async Task ChunkAsync_ShortText_ReturnsSingleChunk()
    {
        var section = CreateSection("Hello.");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(chunks);
        Assert.Equal("Hello.", chunks[0].Text);
    }

    [Fact]
    public async Task ChunkAsync_EmptyText_ReturnsNoChunks()
    {
        var section = CreateSection("");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkAsync_PreservesDocumentIdAndChunkIndex()
    {
        var text = "First paragraph.\n\nSecond paragraph.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(chunks, c => Assert.Equal("doc-1", c.DocumentId));
        Assert.Equal(0, chunks[0].ChunkIndex);
        Assert.Equal(1, chunks[1].ChunkIndex);
    }

    [Fact]
    public async Task ChunkAsync_WithOverlap_ChunksOverlap()
    {
        var text = "First paragraph.\n\nSecond paragraph.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 5 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("First paragraph.", chunks[0].Text);
        // The second chunk should start with the last 5 chars of the first chunk's text
        var expectedOverlap = chunks[0].Text[^5..];
        Assert.StartsWith(expectedOverlap, chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkAsync_TracksPositionsRelativeToSource()
    {
        var text = "First paragraph.\n\nSecond paragraph.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);

        // First chunk: "First paragraph." starts at 0 in source
        Assert.Equal(0, chunks[0].StartPosition);
        Assert.Equal("First paragraph.".Length, chunks[0].EndPosition);

        // Second chunk: "Second paragraph." starts after "\n\n" separator
        var expectedStart = text.IndexOf("Second paragraph.", StringComparison.Ordinal);
        Assert.Equal(expectedStart, chunks[1].StartPosition);
        Assert.Equal(expectedStart + "Second paragraph.".Length, chunks[1].EndPosition);
    }

    [Fact]
    public async Task ChunkAsync_TextShorterThanMaxChunkSize_IsNotSplitAtAll()
    {
        var ct = TestContext.Current.CancellationToken;
        // 35 characters against a 512 limit. There is nothing to split.
        var section = CreateSection("First paragraph.\n\nSecond paragraph.");
        var options = new ChunkingOptions { MaxChunkSize = 512, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        Assert.Single(chunks);
        Assert.Equal("First paragraph.\n\nSecond paragraph.", chunks[0].Text);
    }

    [Fact]
    public async Task ChunkAsync_NoSeparatorFound_HardSplitsAtMaxSize()
    {
        var ct = TestContext.Current.CancellationToken;
        // 25 'a' chars with MaxChunkSize=10 and no valid separator — must hard-split
        var text = new string('a', 25);
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 10, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        Assert.True(chunks.Count >= 2, "Hard split must produce multiple chunks");
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 10, $"Chunk too long: '{c.Text}'"));
        Assert.Equal(text, string.Concat(chunks.Select(c => c.Text)));
    }

    [Fact]
    public async Task ChunkAsync_ManyShortLines_PacksThemTowardsMaxChunkSize()
    {
        var ct = TestContext.Current.CancellationToken;
        // 60 lines of ~16 characters = 1,010 characters against a 512 limit.
        var text = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"- item number {i}"));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 512, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        // Before this task: 60 chunks averaging 31 characters.
        Assert.InRange(chunks.Count, 2, 3);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 512, $"Chunk of {c.Text.Length} exceeds 512"));
    }

    [Fact]
    public async Task ChunkAsync_WordsWithNoSentenceOrLineBreak_DoNotBecomeOneChunkPerWord()
    {
        var ct = TestContext.Current.CancellationToken;
        // 150 words, no ". " and no newline, so the recursion reaches the " " separator.
        var text = string.Join(" ", Enumerable.Repeat("word", 150));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 512, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        // Before this task: 150 chunks of 4 characters. This is the case that proves it is a defect.
        Assert.InRange(chunks.Count, 2, 3);
        // Greedy packing fills every chunk towards MaxChunkSize; only the final
        // chunk may carry the short tail (749 chars total cannot make two chunks
        // that both exceed 400).
        Assert.All(chunks[..^1], c => Assert.True(c.Text.Length > 400, $"Chunk of {c.Text.Length} was not packed"));
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 512, $"Chunk of {c.Text.Length} exceeds 512"));
    }

    [Fact]
    public async Task ChunkAsync_SentencesArePackedAndKeepTheirSeparators()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = "Alpha one. Bravo two. Charlie three. Delta four.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 30, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        // Sentences pack until the next will not fit, and the ". " between packed
        // sentences is restored rather than dropped.
        Assert.Contains(chunks, c => c.Text.Contains(". ", StringComparison.Ordinal));
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 30, $"Chunk of {c.Text.Length} exceeds 30"));
    }

    [Fact]
    public async Task ChunkAsync_PartsThatExactlyFillMaxChunkSize_PackIntoOneChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        // Exact-boundary pin for the fit check `buffer + separator + next <= maxSize`.
        // Lines of 9 and 10 chars joined by "\n" (1 char): 9 + 1 + 10 = 20 = MaxChunkSize
        // exactly — the case where `<=` and `<` disagree. The third 10-char line makes
        // the whole text 31 chars so splitting actually happens, and it cannot join
        // (20 + 1 + 10 = 31 > 20), so it must land alone in the second chunk.
        var text = new string('a', 9) + "\n" + new string('b', 10) + "\n" + new string('c', 10);
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new string('a', 9) + "\n" + new string('b', 10), chunks[0].Text);
        Assert.Equal(20, chunks[0].Text.Length);
        Assert.Equal(new string('c', 10), chunks[1].Text);
    }

    [Fact]
    public async Task ChunkAsync_PartsOneCharOverMaxChunkSize_SplitIntoTwoChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        // The other side of the boundary: two 10-char lines joined by "\n" (1 char)
        // sum to 10 + 1 + 10 = 21 = MaxChunkSize + 1. Only the separator's own length
        // pushes this over the limit, so a fit check that forgets `separator.Length`
        // (10 + 10 = 20 <= 20) would pack them into a 21-char chunk exceeding MaxChunkSize.
        var text = new string('a', 10) + "\n" + new string('b', 10);
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new string('a', 10), chunks[0].Text);
        Assert.Equal(new string('b', 10), chunks[1].Text);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 20, $"Chunk of {c.Text.Length} exceeds 20"));
    }

    [Theory]
    [InlineData("alpha  \n\n  bravo charlie delta echo")]
    [InlineData("alpha\n\n\n\nbravo\n\n\n\ncharlie delta")]
    [InlineData("  leading and trailing  \n\nsecond one here  ")]
    [InlineData("one.  two.   three. four. five. six.")]
    [InlineData("tab\there\n\nand\tthere plus more words")]
    public async Task ChunkAsync_EveryChunkIsAnExactSubstringOfTheSource(string text)
    {
        var ct = TestContext.Current.CancellationToken;
        var section = CreateSection(text);
        // Small enough to force splitting, Overlap 0 so chunk text is unmodified.
        var options = new ChunkingOptions { MaxChunkSize = 8, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        Assert.All(chunks, c => Assert.True(
            text.Contains(c.Text, StringComparison.Ordinal),
            $"Chunk \"{c.Text}\" does not appear in the source \"{text}\""));
    }

    [Theory]
    [InlineData("a  \n\n  b  \n\n  c")]
    [InlineData("  leading  \n\nsecond  \n\nthird")]
    [InlineData("one.  two.   three.")]
    public async Task ChunkAsync_PositionsPointAtTheChunkText(string text)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new ChunkingOptions { MaxChunkSize = 8, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(CreateSection(text), options, ct).ToListAsync(ct);

        // Guard: an input that no longer splits would exercise nothing.
        Assert.True(chunks.Count > 1, $"Input must produce multiple chunks; got {chunks.Count}");

        var previousEnd = 0;
        foreach (var chunk in chunks)
        {
            Assert.InRange(chunk.StartPosition, 0, text.Length);
            Assert.InRange(chunk.EndPosition, chunk.StartPosition, text.Length);
            Assert.Equal(
                chunk.Text,
                text[chunk.StartPosition..chunk.EndPosition]);
            // At Overlap 0 chunks partition the source in order: a chunk may not
            // start before the previous one ended, or a duplicate occurrence
            // earlier in the text could masquerade as this chunk's position.
            Assert.True(
                chunk.StartPosition >= previousEnd,
                $"Chunk at [{chunk.StartPosition}..{chunk.EndPosition}] starts before previous end {previousEnd}");
            previousEnd = chunk.EndPosition;
        }
    }

    [Fact]
    public async Task ChunkAsync_EveryChunkIsASubstring_AcrossGeneratedWhitespaceShapes()
    {
        var ct = TestContext.Current.CancellationToken;
        var pieces = new[] { "alpha", "b", "  ", "\n", "\n\n", ". ", " ", "gamma delta", "\t", "x." };
        var random = new Random(20260731); // fixed seed — a failure must be reproducible

        for (var iteration = 0; iteration < 500; iteration++)
        {
            var builder = new StringBuilder();
            var pieceCount = random.Next(1, 40);
            for (var i = 0; i < pieceCount; i++)
            {
                builder.Append(pieces[random.Next(pieces.Length)]);
            }

            var text = builder.ToString();
            var options = new ChunkingOptions { MaxChunkSize = random.Next(4, 64), Overlap = 0 };
            var chunks = await _sut.ChunkAsync(CreateSection(text), options, ct).ToListAsync(ct);

            var previousEnd = 0;
            foreach (var chunk in chunks)
            {
                Assert.True(
                    text.Contains(chunk.Text, StringComparison.Ordinal),
                    $"iteration {iteration}: chunk \"{chunk.Text}\" not in source \"{text}\"");
                Assert.True(
                    chunk.Text.Length <= options.MaxChunkSize,
                    $"iteration {iteration}: chunk of {chunk.Text.Length} exceeds {options.MaxChunkSize}");
                Assert.InRange(chunk.StartPosition, 0, text.Length);
                Assert.InRange(chunk.EndPosition, chunk.StartPosition, text.Length);
                Assert.True(
                    string.Equals(chunk.Text, text[chunk.StartPosition..chunk.EndPosition], StringComparison.Ordinal),
                    $"iteration {iteration}: positions [{chunk.StartPosition}..{chunk.EndPosition}] point at \"{text[chunk.StartPosition..chunk.EndPosition]}\" not \"{chunk.Text}\"");
                // The pieces repeat, so duplicate text is abundant: a search that
                // matched an earlier occurrence would pass the substring check yet
                // report a position before the previous chunk's end. At Overlap 0
                // positions must strictly advance.
                Assert.True(
                    chunk.StartPosition >= previousEnd,
                    $"iteration {iteration}: chunk at [{chunk.StartPosition}..{chunk.EndPosition}] starts before previous end {previousEnd} in \"{text}\"");
                previousEnd = chunk.EndPosition;
            }
        }
    }
}
