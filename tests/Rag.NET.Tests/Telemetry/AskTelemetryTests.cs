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
        // gen_ai.operation.name is always set. gen_ai.request.stream is only set by the
        // streaming overload — see AskStreamingAsync_EmitsAskSpan. gen_ai.provider.name and
        // gen_ai.request.model are left unset here: the bare NSubstitute IChatClient exposes no
        // ChatClientMetadata, and ChatAnswerEngine must not fabricate values it cannot obtain.
        Assert.Equal("chat", span.GetTagItem("gen_ai.operation.name"));
        Assert.Null(span.GetTagItem("gen_ai.request.stream"));
        Assert.Null(span.GetTagItem("gen_ai.provider.name"));
        Assert.Null(span.GetTagItem("gen_ai.request.model"));
    }

    [Fact]
    public async Task AskAsync_WithChatClientMetadata_TagsProviderAndModel()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Paris.")));
        // ChatClientMetadata is what a real provider (e.g. the OpenAI or Ollama client)
        // exposes through GetService — this is the value ChatAnswerEngine reads for
        // gen_ai.provider.name / gen_ai.request.model when it is actually available.
        chatClient.GetService(typeof(ChatClientMetadata), null)
            .Returns(new ChatClientMetadata("openai", defaultModelId: "gpt-4o"));

        var engine = new ChatAnswerEngine(chatClient);
        var sources = Array.Empty<SearchResult>();

        using var parent = new Activity("test-parent").Start();

        await engine.AskAsync("Where is the Eiffel Tower?", sources,
            cancellationToken: TestContext.Current.CancellationToken);

        var span = activities
            .Where(a => a.TraceId == parent.TraceId)
            .FirstOrDefault(a => string.Equals(a.OperationName, "ragnet.ask", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Equal("openai", span.GetTagItem("gen_ai.provider.name"));
        Assert.Equal("gpt-4o", span.GetTagItem("gen_ai.request.model"));
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
        Assert.Equal("chat", span.GetTagItem("gen_ai.operation.name"));
        // Only the streaming overload sets gen_ai.request.stream — see the non-streaming
        // counterpart in AskAsync_EmitsAskSpan, which asserts it stays unset there.
        Assert.Equal(true, span.GetTagItem("gen_ai.request.stream"));
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
