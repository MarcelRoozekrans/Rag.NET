using Rag.NET.VectorStores;
using Xunit;

namespace Rag.NET.Tests.VectorStores;

/// <summary>
/// Covers <see cref="VectorStoreInitialisationGate"/>, the shared first-use initialisation used by
/// every vector store (#353).
/// </summary>
/// <remarks>
/// The gate's source is linked into this project the same way it is linked into each store, because
/// it is <c>internal</c> to whichever assembly compiles it and no store assembly grants
/// <c>InternalsVisibleTo</c> here. Testing it directly is the point: reaching it through a store
/// would need that store's backend in a container, and the behaviour worth pinning — retry after a
/// failure — is precisely the one a container test would never exercise.
/// </remarks>
public class VectorStoreInitialisationGateTests
{
    [Fact]
    public async Task EnsureInitialisedAsync_OverManyCalls_InitialisesOnce()
    {
        using var gate = new VectorStoreInitialisationGate();
        int calls = 0;

        for (int i = 0; i < 5; i++)
        {
            await gate.EnsureInitialisedAsync(_ => { calls++; return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EnsureInitialisedAsync_UnderConcurrentFirstUse_InitialisesOnce()
    {
        // The case the semaphore exists for: a singleton store taking its first concurrent
        // requests. Without it, every one of these would race into a create.
        using var gate = new VectorStoreInitialisationGate();
        int calls = 0;

        async Task Initialise(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(20, ct);
        }

        var racers = Enumerable.Range(0, 32)
            .Select(_ => gate.EnsureInitialisedAsync(Initialise, TestContext.Current.CancellationToken));
        await Task.WhenAll(racers);

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task EnsureInitialisedAsync_WhenInitialisationThrows_RetriesOnTheNextCall()
    {
        // The reason this is a gate and not a Lazy<Task>. A Lazy<Task> caches the faulted task, so
        // one transient failure at startup would leave the store broken for the life of the
        // process — and every later call would fail without ever reaching the backend again.
        using var gate = new VectorStoreInitialisationGate();
        int attempts = 0;

        Task Initialise(CancellationToken ct)
        {
            attempts++;
            return attempts == 1
                ? Task.FromException(new InvalidOperationException("transient"))
                : Task.CompletedTask;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.EnsureInitialisedAsync(Initialise, TestContext.Current.CancellationToken));

        await gate.EnsureInitialisedAsync(Initialise, TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task EnsureInitialisedAsync_AfterAFailure_StillOnlySucceedsOnce()
    {
        using var gate = new VectorStoreInitialisationGate();
        int attempts = 0;

        Task Initialise(CancellationToken ct)
        {
            attempts++;
            return attempts == 1 ? Task.FromException(new InvalidOperationException("transient")) : Task.CompletedTask;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.EnsureInitialisedAsync(Initialise, TestContext.Current.CancellationToken));
        await gate.EnsureInitialisedAsync(Initialise, TestContext.Current.CancellationToken);
        await gate.EnsureInitialisedAsync(Initialise, TestContext.Current.CancellationToken);

        // Two: the failure and the success. The third call rides the cached success.
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Reset_MakesTheNextUseInitialiseAgain()
    {
        // What a store calls when its collection is dropped through ICollectionManageable. Without
        // it the gate would keep believing the index exists and every later write would target
        // something that had been deleted.
        using var gate = new VectorStoreInitialisationGate();
        int calls = 0;

        await gate.EnsureInitialisedAsync(_ => { calls++; return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        gate.Reset();
        await gate.EnsureInitialisedAsync(_ => { calls++; return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
    }
}
