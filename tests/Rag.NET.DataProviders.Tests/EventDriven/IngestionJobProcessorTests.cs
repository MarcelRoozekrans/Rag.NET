using System.Text;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders.EventDriven;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Tests.EventDriven;

public sealed class IngestionJobProcessorTests
{
    private static IngestionJob MakeJob(string id) => new()
    {
        Content = Encoding.UTF8.GetBytes($"content of {id}"),
        Metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId(id),
            FileName = $"{id}.txt",
        },
    };

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Result<IngestionResult, RagError> Success(string id) =>
        Result<IngestionResult, RagError>.Success(new IngestionResult
        {
            DocumentId = new DocumentId(id),
            ChunksStored = 1,
        });

    /// <summary>
    /// Substituted ingestor recording (documentId, content) per call and signalling
    /// <paramref name="allDone"/> once <paramref name="expectedCalls"/> calls arrived.
    /// <paramref name="perCallResult"/> decides each call's outcome (may throw).
    /// </summary>
    private static IIngestor MakeRecordingIngestor(
        List<(string DocumentId, string Content)> calls,
        int expectedCalls,
        TaskCompletionSource allDone,
        Func<string, Result<IngestionResult, RagError>>? perCallResult = null)
    {
        var ingestor = Substitute.For<IIngestor>();
        ingestor.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var documentId = call.Arg<DocumentMetadata>()!.DocumentId.Value;
                using var reader = new StreamReader(call.Arg<Stream>()!, Encoding.UTF8, leaveOpen: true);
                var content = reader.ReadToEnd();
                try
                {
                    lock (calls)
                    {
                        calls.Add((documentId, content));
                    }

                    var result = perCallResult?.Invoke(documentId) ?? Success(documentId);
                    return Task.FromResult(result);
                }
                finally
                {
                    lock (calls)
                    {
                        if (calls.Count == expectedCalls)
                            allDone.TrySetResult();
                    }
                }
            });
        return ingestor;
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesAllEnqueuedJobs_WithMatchingMetadataAndContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new ChannelIngestionJobQueue(new EventDrivenIngestionOptions());
        var calls = new List<(string DocumentId, string Content)>();
        var allDone = NewTcs();
        var ingestor = MakeRecordingIngestor(calls, expectedCalls: 3, allDone);

        var sut = new IngestionJobProcessor(queue, ingestor);
        await sut.StartAsync(ct);
        try
        {
            await queue.EnqueueAsync(MakeJob("doc-1"), ct);
            await queue.EnqueueAsync(MakeJob("doc-2"), ct);
            await queue.EnqueueAsync(MakeJob("doc-3"), ct);
            await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        Assert.Equal(
            [("doc-1", "content of doc-1"), ("doc-2", "content of doc-2"), ("doc-3", "content of doc-3")],
            calls);
    }

    [Fact]
    public async Task ExecuteAsync_JobWithFailureResult_DoesNotStopSubsequentJobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new ChannelIngestionJobQueue(new EventDrivenIngestionOptions());
        var calls = new List<(string DocumentId, string Content)>();
        var allDone = NewTcs();
        var ingestor = MakeRecordingIngestor(calls, expectedCalls: 3, allDone,
            perCallResult: id => string.Equals(id, "doc-2", StringComparison.Ordinal)
                ? Result<IngestionResult, RagError>.Failure(new RagError.NonSeekableStream())
                : Success(id));

        var sut = new IngestionJobProcessor(queue, ingestor);
        await sut.StartAsync(ct);
        try
        {
            await queue.EnqueueAsync(MakeJob("doc-1"), ct);
            await queue.EnqueueAsync(MakeJob("doc-2"), ct);
            await queue.EnqueueAsync(MakeJob("doc-3"), ct);
            await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        Assert.Equal(["doc-1", "doc-2", "doc-3"], calls.Select(c => c.DocumentId), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_JobThatThrows_DoesNotStopSubsequentJobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new ChannelIngestionJobQueue(new EventDrivenIngestionOptions());
        var calls = new List<(string DocumentId, string Content)>();
        var allDone = NewTcs();
        var ingestor = MakeRecordingIngestor(calls, expectedCalls: 3, allDone,
            perCallResult: id => string.Equals(id, "doc-2", StringComparison.Ordinal)
                ? throw new InvalidOperationException("ingestion exploded")
                : Success(id));

        var sut = new IngestionJobProcessor(queue, ingestor);
        await sut.StartAsync(ct);
        try
        {
            await queue.EnqueueAsync(MakeJob("doc-1"), ct);
            await queue.EnqueueAsync(MakeJob("doc-2"), ct);
            await queue.EnqueueAsync(MakeJob("doc-3"), ct);
            await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        Assert.Equal(["doc-1", "doc-2", "doc-3"], calls.Select(c => c.DocumentId), StringComparer.Ordinal);
    }

    [Fact]
    public async Task StopAsync_MidQueue_ShutsDownCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new ChannelIngestionJobQueue(new EventDrivenIngestionOptions());
        var firstStarted = NewTcs();

        var ingestor = Substitute.For<IIngestor>();
        ingestor.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                firstStarted.TrySetResult();
                // Block until the processor's stopping token cancels the in-flight ingest.
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return Success("never");
            });

        var sut = new IngestionJobProcessor(queue, ingestor);
        await sut.StartAsync(ct);
        await queue.EnqueueAsync(MakeJob("doc-1"), ct);
        await queue.EnqueueAsync(MakeJob("doc-2"), ct);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Must complete promptly and without surfacing an exception: the stopping token
        // cancels the blocked ingest and ExecuteAsync exits via its clean-shutdown catch.
        await sut.StopAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(sut.ExecuteTask is { IsCompletedSuccessfully: true });
    }
}
