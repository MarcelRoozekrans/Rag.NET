using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// A hand-written <see cref="IIngestor"/> rather than an NSubstitute mock, following the
/// EventDriven suites: EPS06 is an error in this repo and fires on any test that faults a
/// <c>ValueTask</c>-returning member, so the fakes are written out.
/// </summary>
internal sealed class FakeIngestor : IIngestor
{
    private readonly Func<DocumentMetadata, CancellationToken, Task<Result<IngestionResult, RagError>>> _behaviour;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<DocumentMetadata> _ingested = [];
    private int _calls;

    private FakeIngestor(Func<DocumentMetadata, CancellationToken, Task<Result<IngestionResult, RagError>>> behaviour) =>
        _behaviour = behaviour;

    /// <summary>Completes as soon as <see cref="IngestAsync"/> is entered.</summary>
    public Task Entered => _entered.Task;

    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<DocumentMetadata> Ingested
    {
        get
        {
            lock (_ingested)
            {
                return [.. _ingested];
            }
        }
    }

    public static FakeIngestor Succeeding() => new((metadata, _) =>
        Task.FromResult(Result<IngestionResult, RagError>.Success(new IngestionResult
        {
            DocumentId = metadata.DocumentId,
            ChunksStored = 1,
        })));

    public static FakeIngestor Failing(RagError error) =>
        new((_, _) => Task.FromResult(Result<IngestionResult, RagError>.Failure(error)));

    public static FakeIngestor Throwing(Exception exception) =>
        new((_, _) => Task.FromException<Result<IngestionResult, RagError>>(exception));

    /// <summary>Blocks until its cancellation token fires — the in-flight-at-shutdown case.</summary>
    public static FakeIngestor Blocking() => new(async (_, cancellationToken) =>
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("A blocking ingestor must never complete normally.");
    });

    public Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        lock (_ingested)
        {
            _ingested.Add(metadata);
        }

        _entered.TrySetResult();
        return _behaviour(metadata, cancellationToken);
    }

    public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
