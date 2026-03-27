using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Memory;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Memory;

public class PersistentConversationMemoryTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> MockEmbedder(float[] vector)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(vector)]));
        return embedder;
    }

    private static IConversationMemory PassthroughInner()
    {
        var inner = Substitute.For<IConversationMemory>();
        inner.ProcessAsync(Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<CancellationToken>())
             .Returns(ci => ci.Arg<IReadOnlyList<ChatMessage>>());
        return inner;
    }

    private static SearchResult MakeMatch(string text, double score = 0.9) =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId("s1"), ChunkIndex = 0 },
            Score = score,
        };

    [Fact]
    public async Task ProcessAsync_MatchesFound_PrependsPrefixSystemMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
                   .Returns(new[] { MakeMatch("User: Hi\nAssistant: Hello") });

        var sut = new PersistentConversationMemory(
            PassthroughInner(), vectorStore, MockEmbedder([0.1f]), new PersistentMemoryOptions());

        var result = await sut.ProcessAsync([new ChatMessage(ChatRole.User, "Hello")], ct);

        Assert.True(result.Count >= 2);
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Contains("From a previous conversation", result[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_NoMatches_HistoryPassedThroughUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
                   .Returns(Array.Empty<SearchResult>());
        var inner = PassthroughInner();

        var sut = new PersistentConversationMemory(
            inner, vectorStore, MockEmbedder([0.1f]), new PersistentMemoryOptions());

        var history = new[] { new ChatMessage(ChatRole.User, "Hi") };
        var result = await sut.ProcessAsync(history, ct);

        Assert.DoesNotContain(result, m => m.Role == ChatRole.System);
        await inner.Received(1).ProcessAsync(history, ct);
    }

    [Fact]
    public async Task ProcessAsync_BelowMinScore_FilteredOut_NoPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
                   .Returns(new[] { MakeMatch("old exchange", score: 0.3) }); // below 0.7

        var sut = new PersistentConversationMemory(
            PassthroughInner(), vectorStore, MockEmbedder([0.1f]),
            new PersistentMemoryOptions { MinScore = 0.7 });

        var result = await sut.ProcessAsync([new ChatMessage(ChatRole.User, "Hi")], ct);

        Assert.DoesNotContain(result, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task StoreAsync_EmbeddsAndStoresWithCorrectTextAndSessionId()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();

        var sut = new PersistentConversationMemory(
            Substitute.For<IConversationMemory>(), vectorStore,
            MockEmbedder([0.5f]), new PersistentMemoryOptions());

        await sut.StoreAsync("Hello", "Hi there", "session-42", ct);

        await vectorStore.Received(1).StoreAsync(
            Arg.Is<IReadOnlyList<EmbeddedChunk>>(chunks =>
                chunks.Count == 1 &&
                chunks[0].Chunk.Text.Contains("User: Hello",         StringComparison.Ordinal) &&
                chunks[0].Chunk.Text.Contains("Assistant: Hi there", StringComparison.Ordinal) &&
                chunks[0].Chunk.DocumentId.Value == "session-42"),
            ct);
    }

    [Fact]
    public async Task ProcessAsync_SearchFails_CallsInnerWithOriginalHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), ct)
                   .ThrowsAsync(new InvalidOperationException("vector store down"));
        var inner = PassthroughInner();

        var sut = new PersistentConversationMemory(
            inner, vectorStore, MockEmbedder([0.1f]), new PersistentMemoryOptions());

        var history = new[] { new ChatMessage(ChatRole.User, "Hi") };
        var result = await sut.ProcessAsync(history, ct);

        Assert.DoesNotContain(result, m => m.Role == ChatRole.System);
        await inner.Received(1).ProcessAsync(history, ct);
    }
}
