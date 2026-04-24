using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques;
using Xunit;

namespace Rag.NET.Tests.QueryTechniques.ContextualCompression;

/// <summary>
/// End-to-end integration test: verifies that when <c>UseContextualCompression</c> is
/// configured with the default <see cref="Rag.NET.QueryTechniques.ContextualCompression.ExtractiveCompressor"/>,
/// the compressed (sentence-filtered) text actually reaches the LLM prompt — not merely that
/// the compressor is invoked.
/// </summary>
public class ContextualCompressionIntegrationTests
{
    private const string MixedChunkText = "Cats purr loudly. Rockets go to Mars. Cats sleep often.";

    [Fact]
    public async Task AskAsync_WithExtractiveCompression_LlmPromptContainsOnlyRelevantSentences()
    {
        var capturedMessages = new List<ChatMessage>();
        var chat = CreateCapturingChatClient(capturedMessages);
        var embedder = CreateDeterministicEmbedder();
        var vectorStore = CreateVectorStoreReturning(MixedChunkText);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(chat);
        services.AddSingleton(embedder);
        services.AddSingleton(vectorStore);
        services.AddRagNet(rag => rag.UseContextualCompression(o => o.KeepTopSentences = 1));

        await using var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        // Act
        await pipeline.AskAsync("tell me about cats", options: null, TestContext.Current.CancellationToken);

        // Assert: the user-role prompt delivered to the LLM should contain the cats
        // sentence but not the rockets sentence — proving the extractive compressor's
        // output actually replaced the raw chunk text en route to the chat client.
        var userMessages = capturedMessages.Where(m => m.Role == ChatRole.User).ToList();
        Assert.NotEmpty(userMessages);
        var combined = string.Join(" ", userMessages.Select(m => m.Text ?? string.Empty));
        Assert.Contains("Cats", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Rockets", combined, StringComparison.Ordinal);
    }

    private static IChatClient CreateCapturingChatClient(List<ChatMessage> capturedMessages)
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedMessages.AddRange(ci.Arg<IList<ChatMessage>>());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
            });
        return chat;
    }

    // Deterministic embedder: "cats" -> [1,0,0], "rockets" -> [-1,0,0], else zero.
    // Mirrors ExtractiveCompressorTests so the compressor reliably keeps the cats
    // sentence and drops the rockets sentence.
    private static IEmbeddingGenerator<string, Embedding<float>> CreateDeterministicEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbeddings<Embedding<float>>>>(ci =>
            {
                var inputs = ci.Arg<IEnumerable<string>>().ToList();
                var embeddings = inputs.Select(s =>
                {
                    var topic = s.Contains("cats", StringComparison.OrdinalIgnoreCase) ? 1f :
                                s.Contains("rockets", StringComparison.OrdinalIgnoreCase) ? -1f : 0f;
                    return new Embedding<float>(new[] { topic, 0f, 0f });
                }).ToList();
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            });
        return embedder;
    }

    private static IVectorStore CreateVectorStoreReturning(string chunkText)
    {
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>(new[]
            {
                new SearchResult
                {
                    Chunk = new TextChunk
                    {
                        Text = chunkText,
                        DocumentId = new DocumentId("d"),
                        ChunkIndex = 0,
                    },
                    Score = 0.9,
                },
            }));
        return vectorStore;
    }
}
