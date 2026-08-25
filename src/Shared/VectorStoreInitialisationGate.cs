namespace Rag.NET.VectorStores;

/// <summary>
/// Runs a store's one-time initialisation on first use, exactly once, and only caches success.
/// </summary>
/// <remarks>
/// <para>
/// Linked into each vector-store package rather than published from a shared one — see
/// <c>src/Shared/RagTelemetrySource.cs</c> for the same reasoning: one definition, no new
/// dependency, and nothing added to any package's public surface.
/// </para>
/// <para>
/// Modelled on <c>ChromaVectorStore.ResolveCollectionIdAsync</c>, which already did this before
/// #353 was filed: double-checked read, a semaphore, and the result cached after. Chroma reaches
/// its backend through a single <c>get_or_create</c> call, so it never races an exists-probe
/// against a create; stores whose backend has no such operation rely on their own
/// <c>InitializeAsync</c> being idempotent, which is the contract
/// <see cref="Rag.NET.Abstractions.IVectorStore.InitializeAsync"/> states.
/// </para>
/// <para>
/// <b>Failure is deliberately not cached.</b> A <c>Lazy&lt;Task&gt;</c> would be shorter and would
/// hold a faulted task forever, so one transient network error at startup would leave the store
/// permanently broken for the life of the process. Marking success only means the next call
/// retries.
/// </para>
/// </remarks>
internal sealed class VectorStoreInitialisationGate : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _initialised;

    /// <summary>
    /// Invokes <paramref name="initialise"/> once. Concurrent callers wait for the first to
    /// finish rather than each running it.
    /// </summary>
    public async Task EnsureInitialisedAsync(
        Func<CancellationToken, Task> initialise, CancellationToken cancellationToken)
    {
        if (_initialised)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialised)
            {
                return;
            }

            await initialise(cancellationToken).ConfigureAwait(false);

            // Only now. An exception leaves the flag clear, so the next caller tries again.
            _initialised = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Forgets that initialisation happened, so the next use runs it again. Called when the
    /// collection is dropped through <see cref="Rag.NET.Abstractions.ICollectionManageable"/> —
    /// without this, a delete-then-use sequence would keep writing to an index that no longer
    /// exists, because the gate would still believe it had been created.
    /// </summary>
    public void Reset() => _initialised = false;

    public void Dispose() => _lock.Dispose();
}
