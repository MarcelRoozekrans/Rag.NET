using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;
using Xunit;

namespace Rag.NET.Tests.Telemetry;

[Collection("Telemetry")]
public class AskTelemetryTests
{
    private static (ConcurrentBag<Activity> activities, ActivityListener listener) CreateListener()
    {
        var activities = new ConcurrentBag<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, RagTelemetry.SourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return (activities, listener);
    }

    [Fact]
    public async Task AskAsync_EmitsAskSpan()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;

        // Fake IChatClient that returns "Paris."
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Paris.")));

        var engine = new ChatAnswerEngine(chatClient);
        var sources = Array.Empty<SearchResult>();

        // Start a parent activity so the span emitted by THIS test inherits our TraceId
        // (ActivitySource.StartActivity picks up Activity.Current via AsyncLocal). Filtering
        // by TraceId deterministically excludes spans from concurrently running test classes
        // that hit the same global ActivitySource.
        using var parent = new Activity("test-parent").Start();

        await engine.AskAsync("Where is the Eiffel Tower?", sources,
            cancellationToken: TestContext.Current.CancellationToken);

        var span = activities
            .Where(a => a.TraceId == parent.TraceId)
            .FirstOrDefault(a => string.Equals(a.OperationName, "ragnet.ask", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Equal("0", span.GetTagItem("source.count")?.ToString());
        Assert.Equal(SynthesisStrategy.Default.ToString(), span.GetTagItem("synthesis.strategy"));
    }

    [Fact]
    public async Task AskStreamingAsync_EmitsAskSpan()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new ChatResponseUpdate { Contents = [new TextContent("Paris.")] }));

        var engine = new ChatAnswerEngine(chatClient);
        var sources = Array.Empty<SearchResult>();

        // See AskAsync_EmitsAskSpan: TraceId-parent filtering is deterministic under test parallelism.
        using var parent = new Activity("test-parent").Start();

        await foreach (var update in engine.AskStreamingAsync("Where is the Eiffel Tower?", sources,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            // consume the stream fully so the span is stopped
        }

        var span = activities
            .Where(a => a.TraceId == parent.TraceId)
            .FirstOrDefault(a => string.Equals(a.OperationName, "ragnet.ask", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Equal("0", span.GetTagItem("source.count")?.ToString());
        Assert.Equal(SynthesisStrategy.Default.ToString(), span.GetTagItem("synthesis.strategy"));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToAsyncEnumerable(
        params ChatResponseUpdate[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.CompletedTask;
        }
    }
}
