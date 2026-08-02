using Rag.NET.Evaluation.Shadow;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// The trivial implementation behind the store seam. It exists so tests and samples work; the
/// properties worth pinning are that nothing handed to it is lost — including under concurrent
/// writers, which is how the background consumer will use it — and that a snapshot is a snapshot.
/// </summary>
public sealed class InMemoryShadowCaptureStoreTests
{
    [Fact]
    public async Task SaveAsync_KeepsCapturesInArrivalOrder()
    {
        var store = new InMemoryShadowCaptureStore();

        await store.SaveAsync(Capture("q1"), TestContext.Current.CancellationToken);
        await store.SaveAsync(Capture("q2"), TestContext.Current.CancellationToken);

        var captures = store.Snapshot();
        Assert.Equal(2, captures.Count);
        Assert.Equal("q1", captures[0].Question);
        Assert.Equal("q2", captures[1].Question);
    }

    [Fact]
    public async Task Snapshot_IsAPointInTimeCopyRatherThanALiveView()
    {
        var store = new InMemoryShadowCaptureStore();
        await store.SaveAsync(Capture("q1"), TestContext.Current.CancellationToken);

        var before = store.Snapshot();
        await store.SaveAsync(Capture("q2"), TestContext.Current.CancellationToken);

        // An offline comparison iterates the snapshot while production keeps writing; a live view
        // would make that iteration race the very traffic it is analysing.
        Assert.Single(before);
        Assert.Equal(2, store.Snapshot().Count);
    }

    [Fact]
    public async Task SaveAsync_UnderConcurrentWriters_LosesNothing()
    {
        var store = new InMemoryShadowCaptureStore();

        var writers = new Task[100];
        for (var i = 0; i < writers.Length; i++)
        {
            var question = $"q{i}";
            writers[i] = Task.Run(
                () => store.SaveAsync(Capture(question), TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(writers);

        Assert.Equal(100, store.Snapshot().Count);
    }

    [Fact]
    public async Task SaveAsync_NullCapture_Throws()
    {
        var store = new InMemoryShadowCaptureStore();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.SaveAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_WithACancelledToken_ThrowsAndStoresNothing()
    {
        var store = new InMemoryShadowCaptureStore();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(Capture("q1"), cancellation.Token));

        Assert.Empty(store.Snapshot());
    }

    private static ShadowCapture Capture(string question) => new(
        question,
        new ShadowVariantCapture("primary", Answer: "a", ContextTexts: ["c"]),
        new ShadowVariantCapture("shadow", Answer: "b", ContextTexts: ["d"]),
        new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
}
