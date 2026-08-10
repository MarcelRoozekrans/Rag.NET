using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class PropositionChunkingStrategyTests
{
    private static DocumentSection Section(string text, string docId = "doc1") =>
        new() { DocumentId = new DocumentId(docId), Text = text };

    private static async IAsyncEnumerable<DocumentSection> ToAsync(IEnumerable<DocumentSection> sections)
    {
        foreach (var s in sections) yield return s;
        await Task.CompletedTask;
    }

    private static IChatClient ChatReturning(string responseText)
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText))));
        return chat;
    }

    private static PropositionChunkingStrategy MakeSut(IChatClient chat, PropositionChunkingOptions? opts = null) =>
        new(chat, opts ?? new PropositionChunkingOptions(), NullLogger<PropositionChunkingStrategy>.Instance);

    [Fact]
    public void Options_Defaults_AreCorrect()
    {
        var options = new PropositionChunkingOptions();

        Assert.Equal(1000, options.MaxPassageTokens);
        Assert.Equal(50, options.MaxPropositionsPerPassage);
        Assert.False(options.EmitParentPassages);
        Assert.Null(options.ChatClient);
    }

    [Fact]
    public async Task WellFormedResponse_OnePropositionPerChunk_WithParentMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        const string text = "Fact one is stated here. Fact two is stated there.";
        var sut = MakeSut(ChatReturning("""["Fact one.","Fact two."]"""));

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section(text)]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Fact one.", chunks[0].Text);
        Assert.Equal("Fact two.", chunks[1].Text);
        Assert.All(chunks, c =>
        {
            Assert.Equal<MetadataValue>("proposition", c.Metadata["chunk.kind"]);
            Assert.Equal(0, int.Parse(c.Metadata["parent.start"].ToString(), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(text.Length, int.Parse(c.Metadata["parent.end"].ToString(), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(0, c.StartPosition);
            Assert.Equal(text.Length, c.EndPosition);
            Assert.Equal(new DocumentId("doc1"), c.DocumentId);
        });
        Assert.Equal(0, chunks[0].ChunkIndex);
        Assert.Equal(1, chunks[1].ChunkIndex);
    }

    private const string PropositionPayload = """["Fact one.","Fact two."]""";

    public static TheoryData<string, string> WrappedResponseShapes() => new()
    {
        { "preamble then unlabelled fence", $"Here are the propositions in JSON format:\n\n```\n{PropositionPayload}\n```" },
        { "preamble then labelled fence", $"Sure! Here you go:\n\n```json\n{PropositionPayload}\n```" },
        { "preamble then bare array", $"Here are the propositions:\n\n{PropositionPayload}" },
        { "fence then trailing prose", $"```\n{PropositionPayload}\n```\n\nLet me know if you need anything else." },
    };

    [Theory]
    [MemberData(nameof(WrappedResponseShapes))]
    public async Task WhenLlmWrapsTheArray_PropositionsStillEmerge(string shape, string response)
    {
        // The old strip ran only when the response STARTED with a fence, so each of these shapes
        // threw in JsonNode.Parse and silently downgraded the passage to a fallback chunk —
        // green pipeline, no propositions, one warning.
        var ct = TestContext.Current.CancellationToken;
        const string text = "Fact one is stated here. Fact two is stated there.";
        var sut = MakeSut(ChatReturning(response));

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section(text)]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.True(
            chunks.Count == 2 && chunks[0].Metadata["chunk.kind"] == (MetadataValue)"proposition",
            $"The '{shape}' response produced {chunks.Count} chunk(s) of kind " +
            $"'{chunks[0].Metadata["chunk.kind"]}'. The array inside it is valid, so parsing " +
            "failed to find it and the passage silently fell back.");
        Assert.Equal("Fact one.", chunks[0].Text);
        Assert.Equal("Fact two.", chunks[1].Text);
    }

    [Fact]
    public async Task MalformedJson_FallsBackToPassageChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        const string text = "Some passage text.";
        var sut = MakeSut(ChatReturning("not json"));

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section(text)]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Single(chunks);
        Assert.Equal(text, chunks[0].Text);
        Assert.Equal<MetadataValue>("passage", chunks[0].Metadata["chunk.kind"]);
    }

    [Fact]
    public async Task LlmThrows_FallsBackToPassageChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        const string text = "Some passage text.";
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => Task.FromException<ChatResponse>(new InvalidOperationException("boom")));
        var sut = MakeSut(chat);

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section(text)]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Single(chunks);
        Assert.Equal(text, chunks[0].Text);
        Assert.Equal<MetadataValue>("passage", chunks[0].Metadata["chunk.kind"]);
    }

    [Fact]
    public async Task EmptyAndWhitespacePropositions_AreDropped()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = MakeSut(ChatReturning("""["", "  ", "Real."]"""));

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section("Passage.")]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Single(chunks);
        Assert.Equal("Real.", chunks[0].Text);
        Assert.Equal<MetadataValue>("proposition", chunks[0].Metadata["chunk.kind"]);
    }

    [Fact]
    public async Task CapRespected()
    {
        var ct = TestContext.Current.CancellationToken;
        var json = "[" + string.Join(",", Enumerable.Range(1, 60).Select(i => $"\"Proposition {i}.\"")) + "]";
        var sut = MakeSut(ChatReturning(json), new PropositionChunkingOptions { MaxPropositionsPerPassage = 50 });

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section("Passage.")]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Equal(50, chunks.Count);
        Assert.Equal("Proposition 50.", chunks[^1].Text);
    }

    [Fact]
    public async Task EmitParentPassages_True_EmitsPassageChunkFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        const string text = "Fact one is stated here. Fact two is stated there.";
        var sut = MakeSut(
            ChatReturning("""["Fact one.","Fact two."]"""),
            new PropositionChunkingOptions { EmitParentPassages = true });

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section(text)]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Equal(3, chunks.Count);
        Assert.Equal<MetadataValue>("passage", chunks[0].Metadata["chunk.kind"]);
        Assert.Equal(text, chunks[0].Text);
        Assert.Equal<MetadataValue>("proposition", chunks[1].Metadata["chunk.kind"]);
        Assert.Equal<MetadataValue>("proposition", chunks[2].Metadata["chunk.kind"]);
        Assert.Equal([0, 1, 2], chunks.Select(c => c.ChunkIndex));
    }

    [Fact]
    public async Task LongDocument_SplitsIntoMultiplePassages()
    {
        var ct = TestContext.Current.CancellationToken;
        const string text =
            "The quick brown fox jumps over the lazy dog and keeps running through the quiet green forest today.";
        var calls = 0;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """["Fact."]""")));
            });
        var sut = MakeSut(chat, new PropositionChunkingOptions { MaxPassageTokens = 8 });

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section(text)]), new ChunkingOptions(), ct).ToListAsync(ct);

        // One LLM call per passage; each passage yields one proposition chunk.
        Assert.True(calls > 1, $"expected multiple passages, got {calls} LLM call(s)");
        Assert.Equal(calls, chunks.Count);

        // Untrimmed passage spans partition the full text.
        Assert.Equal(0, chunks[0].StartPosition);
        Assert.Equal(text.Length, chunks[^1].EndPosition);
        for (var i = 1; i < chunks.Count; i++)
            Assert.Equal(chunks[i - 1].EndPosition, chunks[i].StartPosition);
    }

    [Fact]
    public async Task ChunkAsync_SingleSectionWrapper_ProducesPropositions()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = MakeSut(ChatReturning("""["Fact one.","Fact two."]"""));

        var chunks = await sut.ChunkAsync(
            Section("Fact one is stated here. Fact two is stated there."), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Fact one.", chunks[0].Text);
        Assert.All(chunks, c => Assert.Equal<MetadataValue>("proposition", c.Metadata["chunk.kind"]));
    }

    [Fact]
    public async Task AllWhitespacePropositions_FallBackToPassageChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        const string text = "Some passage text.";
        var sut = MakeSut(ChatReturning("""["", "   "]"""));

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section(text)]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Single(chunks);
        Assert.Equal(text, chunks[0].Text);
        Assert.Equal<MetadataValue>("passage", chunks[0].Metadata["chunk.kind"]);
    }

    [Fact]
    public async Task EmptyResponse_FallsBackToPassageChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        const string text = "Some passage text.";
        var sut = MakeSut(ChatReturning(""));

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section(text)]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Single(chunks);
        Assert.Equal(text, chunks[0].Text);
        Assert.Equal<MetadataValue>("passage", chunks[0].Metadata["chunk.kind"]);
    }

    [Fact]
    public async Task FencedJsonResponse_ParsesPropositions()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = MakeSut(ChatReturning("```json\n[\"Fact one.\",\"Fact two.\"]\n```"));

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section("Passage.")]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Fact one.", chunks[0].Text);
        Assert.Equal("Fact two.", chunks[1].Text);
        Assert.All(chunks, c => Assert.Equal<MetadataValue>("proposition", c.Metadata["chunk.kind"]));
    }

    [Fact]
    public async Task PassageText_IsSentInsideFencedUserMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        const string text = "The mitochondria is the powerhouse of the cell.";
        List<ChatMessage>? captured = null;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<IEnumerable<ChatMessage>>()!.ToList();
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """["Fact."]""")));
            });
        var sut = MakeSut(chat);

        await sut.ChunkDocumentAsync(
            ToAsync([Section(text)]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.NotNull(captured);
        Assert.Equal(2, captured.Count);
        Assert.Equal(ChatRole.System, captured[0].Role);
        Assert.Equal(ChatRole.User, captured[1].Role);
        Assert.Contains(text, captured[1].Text, StringComparison.Ordinal);
        Assert.Contains("<content-", captured[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiByteContent_PassageWindowsPartitionOriginalTextExactly()
    {
        var ct = TestContext.Current.CancellationToken;
        // Emoji + CJK: cl100k is byte-level BPE, so tiny windows are guaranteed to place
        // boundaries inside multi-byte characters. Offset-based slicing must still produce
        // exact substrings of the original text (no U+FFFD, no drift).
        const string text = "数据处理系统将文本分解为原子命题。😀🚀🌟 Emoji and CJK mix here. 中文句子在这里结束。";
        var sentPassages = new List<string>();
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var user = ci.Arg<IEnumerable<ChatMessage>>()!.Last().Text!;
                // User message format: <content-{delim}>\n{passage}\n</content-{delim}>
                sentPassages.Add(user[(user.IndexOf('\n', StringComparison.Ordinal) + 1)..user.LastIndexOf('\n')]);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """["Fact."]""")));
            });
        var sut = MakeSut(chat, new PropositionChunkingOptions { MaxPassageTokens = 3 });

        var chunks = await sut.ChunkDocumentAsync(
            ToAsync([Section(text)]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.True(sentPassages.Count > 1, $"expected multiple passages, got {sentPassages.Count}");
        Assert.Equal(text, string.Concat(sentPassages));
        Assert.All(sentPassages, p => Assert.DoesNotContain('�', p));

        // Chunk spans partition [0, text.Length) contiguously.
        Assert.Equal(0, chunks[0].StartPosition);
        Assert.Equal(text.Length, chunks[^1].EndPosition);
        for (var i = 1; i < chunks.Count; i++)
            Assert.Equal(chunks[i - 1].EndPosition, chunks[i].StartPosition);
    }

    [Fact]
    public async Task CancelledToken_ThrowsOperationCanceledException()
    {
        var sut = MakeSut(ChatReturning("""["Fact."]"""));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.ChunkDocumentAsync(
                ToAsync([Section("Text.")]), new ChunkingOptions(), cts.Token).ToListAsync(cts.Token));
    }
}
