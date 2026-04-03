using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

file sealed class DroppingGuard : IRetrievalGuard
{
    public IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results) =>
        results.Where(r => !string.Equals(r.Chunk.Text, "drop me", StringComparison.Ordinal)).ToList().AsReadOnly();
}

file sealed class OrderTrackingGuard(List<string> order, string name) : IRetrievalGuard
{
    public IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results)
    {
        order.Add(name);
        return results;
    }
}

public class RetrievalGuardBehaviorTests
{
    private static SearchResult MakeResult(string text) => new()
    {
        Score = 1.0,
        Chunk = new TextChunk { Text = text, DocumentId = new DocumentId("doc1"), ChunkIndex = 0 },
    };

    private static RetrievalContext MakeCtx() => new()
    {
        Query = "test",
        Options = new RetrievalOptions(),
    };

    [Fact]
    public async Task HandleAsync_GuardFiltersResults()
    {
        var behavior = new RetrievalGuardBehavior { Guards = [new DroppingGuard()] };
        var ctx = MakeCtx();

        var results = await behavior.HandleAsync(ctx, CancellationToken.None, (_, _) =>
            ValueTask.FromResult<IReadOnlyList<SearchResult>>(
                [MakeResult("keep me"), MakeResult("drop me")]));

        Assert.Single(results);
        Assert.Equal("keep me", results[0].Chunk.Text);
    }

    [Fact]
    public async Task HandleAsync_NoGuards_PassesThrough()
    {
        var behavior = new RetrievalGuardBehavior { Guards = [] };
        var ctx = MakeCtx();
        var results = await behavior.HandleAsync(ctx, CancellationToken.None, (_, _) =>
            ValueTask.FromResult<IReadOnlyList<SearchResult>>([MakeResult("any")]));
        Assert.Single(results);
    }

    [Fact]
    public async Task HandleAsync_MultipleGuards_ComposedInOrder()
    {
        var order = new List<string>();
        var behavior = new RetrievalGuardBehavior
        {
            Guards = [
                new OrderTrackingGuard(order, "first"),
                new OrderTrackingGuard(order, "second"),
            ]
        };
        await behavior.HandleAsync(MakeCtx(), CancellationToken.None, (_, _) =>
            ValueTask.FromResult<IReadOnlyList<SearchResult>>([]));
        Assert.Equal(["first", "second"], order);
    }
}
