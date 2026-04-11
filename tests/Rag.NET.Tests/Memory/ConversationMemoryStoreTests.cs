using Rag.NET.Abstractions;
using Rag.NET.Memory;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Memory;

public class ConversationMemoryStoreTests
{
    [Fact]
    public async Task StoreAsync_IsNoOp_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        IConversationMemory sut = new ConversationMemoryPipeline(new ConversationMemoryOptions(), chatClient: null);

        await sut.StoreAsync("Hello", "Hi there", new SessionId("session-1"), ct);
    }

    [Fact]
    public async Task StoreAsync_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        IConversationMemory sut = new ConversationMemoryPipeline(new ConversationMemoryOptions(), chatClient: null);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.StoreAsync("Hello", "Hi there", new SessionId("session-1"), cts.Token));
    }
}
