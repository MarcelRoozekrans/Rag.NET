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
    private readonly Dictionary<string, int> _entriesById = new(StringComparer.Ordinal);
    private readonly List<Waiter> _waiters = [];
    private readonly Lock _gate = new();
    private int _calls;

    private FakeIngestor(Func<DocumentMetadata, CancellationToken, Task<Result<IngestionResult, RagError>>> behaviour) =>
        _behaviour = behaviour;

    /// <summary>
    /// Completes on the first entry to <see cref="IngestAsync"/>, whatever document caused it.
    /// Only safe where this ingestor is the sole source of entries — the offline unit tests,
    /// which hand it exactly one message. Anything sharing a broker queue must use
    /// <see cref="WaitFor"/>, or it can wake on a message another test sent.
    /// </summary>
    public Task Entered => _entered.Task;

    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<DocumentMetadata> Ingested
    {
        get
        {
            lock (_gate)
            {
                return [.. _ingested];
            }
        }
    }

    /// <summary>
    /// Completes once <see cref="IngestAsync"/> has been entered <paramref name="entries"/>
    /// times <i>for this document</i>.
    /// </summary>
    /// <remarks>
    /// The wake-up is keyed on the document id for the same reason the assertions are: the
    /// integration tests share one queue, so an unkeyed signal lets a test wake on a stray
    /// message, stop its trigger, and then assert against an <see cref="Ingested"/> list its own
    /// document never reached. Unique ids protected the assertions; this protects the wait.
    /// </remarks>
    /// <param name="documentId">The document whose ingestion is being waited on.</param>
    /// <param name="entries">How many entries to wait for — <c>2</c> proves a redelivery.</param>
    public Task WaitFor(string documentId, int entries = 1)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_entriesById.TryGetValue(documentId, out var seen) && seen >= entries)
                source.SetResult();
            else
                _waiters.Add(new Waiter(documentId, entries, source));
        }

        return source.Task;
    }

    /// <summary>How many times <see cref="IngestAsync"/> was entered for one document.</summary>
    public int CallsFor(string documentId)
    {
        lock (_gate)
        {
            return _entriesById.TryGetValue(documentId, out var seen) ? seen : 0;
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

    /// <summary>
    /// Waits for cancellation and then reports it as a <i>failed Result</i> instead of throwing
    /// — a well-behaved <see cref="IIngestor"/> that observes its token without an exception.
    /// The handler must still recognise this as shutdown rather than settling it as a failure.
    /// </summary>
    /// <param name="error">The error to report once cancellation is observed.</param>
    public static FakeIngestor ReportingCancellationAsFailure(RagError error) =>
        new(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Swallowed on purpose: this fake exists to prove the non-throwing shape.
            }

            return Result<IngestionResult, RagError>.Failure(error);
        });

    /// <summary>
    /// Throws on the first attempt <i>for a given document</i> and succeeds afterwards — the
    /// redelivery path, end to end. Keyed per document so a stray message on a shared queue
    /// cannot spend this fake's one failure.
    /// </summary>
    public static FakeIngestor ThrowingOnceThenSucceeding()
    {
        FakeIngestor? instance = null;
        instance = new FakeIngestor((metadata, _) => instance!.CallsFor(metadata.DocumentId.Value) == 1
            ? Task.FromException<Result<IngestionResult, RagError>>(new IOException("first attempt fails"))
            : Task.FromResult(Result<IngestionResult, RagError>.Success(new IngestionResult
            {
                DocumentId = metadata.DocumentId,
                ChunksStored = 1,
            })));
        return instance;
    }

    public Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        Record(metadata);
        _entered.TrySetResult();

        return _behaviour(metadata, cancellationToken);
    }

    public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private void Record(DocumentMetadata metadata)
    {
        var documentId = metadata.DocumentId.Value;
        List<TaskCompletionSource>? due = null;

        lock (_gate)
        {
            _ingested.Add(metadata);
            var seen = _entriesById.TryGetValue(documentId, out var previous) ? previous + 1 : 1;
            _entriesById[documentId] = seen;

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (!string.Equals(waiter.DocumentId, documentId, StringComparison.Ordinal)
                    || seen < waiter.Entries)
                {
                    continue;
                }

                _waiters.RemoveAt(i);
                (due ??= []).Add(waiter.Source);
            }
        }

        foreach (var source in due ?? [])
            source.TrySetResult();
    }

    private sealed record Waiter(string DocumentId, int Entries, TaskCompletionSource Source);
}
