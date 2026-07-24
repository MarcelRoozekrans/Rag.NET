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
            Assert.Equal("proposition", c.Metadata["chunk.kind"]);
            Assert.Equal(0, int.Parse(c.Metadata["parent.start"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(text.Length, int.Parse(c.Metadata["parent.end"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(0, c.StartPosition);
            Assert.Equal(text.Length, c.EndPosition);
            Assert.Equal(new DocumentId("doc1"), c.DocumentId);
        });
        Assert.Equal(0, chunks[0].ChunkIndex);
        Assert.Equal(1, chunks[1].ChunkIndex);
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
        Assert.Equal("passage", chunks[0].Metadata["chunk.kind"]);
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
        Assert.Equal("passage", chunks[0].Metadata["chunk.kind"]);
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
        Assert.Equal("proposition", chunks[0].Metadata["chunk.kind"]);
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
        Assert.Equal("passage", chunks[0].Metadata["chunk.kind"]);
        Assert.Equal(text, chunks[0].Text);
        Assert.Equal("proposition", chunks[1].Metadata["chunk.kind"]);
        Assert.Equal("proposition", chunks[2].Metadata["chunk.kind"]);
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
