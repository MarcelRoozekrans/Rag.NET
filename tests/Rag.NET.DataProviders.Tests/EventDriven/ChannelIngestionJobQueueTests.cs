using System.Text;
using Rag.NET.DataProviders.EventDriven;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.DataProviders.Tests.EventDriven;

public sealed class ChannelIngestionJobQueueTests
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

    private static async Task<List<IngestionJob>> DequeueAsync(
        ChannelIngestionJobQueue queue, int count, CancellationToken ct)
    {
        var items = new List<IngestionJob>();
        await foreach (var job in queue.DequeueAllAsync(ct))
        {
            items.Add(job);
            if (items.Count == count)
                break;
        }

        return items;
    }

    [Fact]
    public async Task EnqueueDequeue_RoundTripsInFifoOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new ChannelIngestionJobQueue(new EventDrivenIngestionOptions());

        await queue.EnqueueAsync(MakeJob("doc-a"), ct);
        await queue.EnqueueAsync(MakeJob("doc-b"), ct);
        await queue.EnqueueAsync(MakeJob("doc-c"), ct);

        var items = await DequeueAsync(queue, 3, ct);

        Assert.Equal(["doc-a", "doc-b", "doc-c"], items.Select(j => j.Metadata.DocumentId.Value), StringComparer.Ordinal);
    }

    [Fact]
    public async Task EnqueueAsync_WhenFull_BlocksUntilDequeue()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new ChannelIngestionJobQueue(new EventDrivenIngestionOptions { QueueCapacity = 1 });

        await queue.EnqueueAsync(MakeJob("doc-a"), ct);
        var second = queue.EnqueueAsync(MakeJob("doc-b"), ct).AsTask();

        // Bounded at 1: the second enqueue cannot complete while doc-a occupies the slot.
        Assert.False(second.IsCompleted);

        var items = await DequeueAsync(queue, 1, ct);
        Assert.Equal("doc-a", items[0].Metadata.DocumentId.Value);

        // Space freed — the blocked enqueue must now complete.
        await second.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    [Fact]
    public async Task EnqueueAsync_CancelledWhileBlocked_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new ChannelIngestionJobQueue(new EventDrivenIngestionOptions { QueueCapacity = 1 });

        await queue.EnqueueAsync(MakeJob("doc-a"), ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var blocked = queue.EnqueueAsync(MakeJob("doc-b"), cts.Token).AsTask();
        Assert.False(blocked.IsCompleted);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => blocked.WaitAsync(TimeSpan.FromSeconds(10), ct));
    }

    [Fact]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ChannelIngestionJobQueue(new EventDrivenIngestionOptions { QueueCapacity = 0 }));
    }
}
