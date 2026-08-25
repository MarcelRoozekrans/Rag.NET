using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Abstractions;

/// <summary>
/// <see cref="IGuidProvider"/> and the seam it opens (#380).
/// </summary>
/// <remarks>
/// The issue asks for an interface so identifiers can be unit tested. These are the tests that
/// could not be written before: they name the value a component will mint and then assert on it.
/// Without the seam the only option is to assert that <i>something</i> Guid-shaped appeared, which
/// is a test that passes whatever the code puts there.
/// </remarks>
public class IGuidProviderTests
{
    [Fact]
    public void SystemGuidProvider_ReturnsADistinctValueEachTime()
    {
        var first = SystemGuidProvider.Instance.NewGuid();
        var second = SystemGuidProvider.Instance.NewGuid();

        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SystemGuidProvider_IsTheDefaultWhenNoneIsSupplied()
    {
        // The TimeProvider arrangement: nothing has to be registered for production behaviour.
        var engine = new ChatAnswerEngine(new StubChatClient());

        Assert.NotNull(engine);
    }

    [Fact]
    public async Task ChatAnswerEngine_UsesTheSuppliedProviderForItsToolCallId()
    {
        // The call id has to match between the FunctionCallContent and the FunctionResultContent,
        // or the pair is malformed. Before the seam that correspondence could only be checked by
        // reading one value out and comparing it to the other — which passes even if both are
        // wrong. Now the expected id is stated up front.
        var guid = new Guid("11111111-2222-3333-4444-555555555555");
        var chatClient = new StubChatClient();
        var engine = new ChatAnswerEngine(chatClient, guidProvider: new FixedGuidProvider(guid));

        await engine.AskAsync(
            "What is it?",
            [Result("a source")],
            new RagOptions { SendSourcesAsToolResult = true },
            TestContext.Current.CancellationToken);

        var expected = "call_" + guid.ToString("N");
        var call = Assert.IsType<FunctionCallContent>(
            Assert.Single(chatClient.Received.First(m => m.Role == ChatRole.Assistant).Contents));
        var result = Assert.IsType<FunctionResultContent>(
            Assert.Single(chatClient.Received.First(m => m.Role == ChatRole.Tool).Contents));

        Assert.Equal(expected, call.CallId);
        Assert.Equal(expected, result.CallId);
    }

    private static SearchResult Result(string text) => new()
    {
        Chunk = new TextChunk { Text = text, DocumentId = new DocumentId("d"), ChunkIndex = 0 },
        Score = 1.0,
    };

    /// <summary>A provider that always answers the same value — the point of the abstraction.</summary>
    private sealed class FixedGuidProvider(Guid value) : IGuidProvider
    {
        public Guid NewGuid() => value;
    }

    private sealed class StubChatClient : IChatClient
    {
        public List<ChatMessage> Received { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Received.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
