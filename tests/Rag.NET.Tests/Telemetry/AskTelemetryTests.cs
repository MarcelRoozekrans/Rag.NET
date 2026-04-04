using System.Diagnostics;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Telemetry;
using Xunit;

namespace Rag.NET.Tests.Telemetry;

public class AskTelemetryTests
{
    private static (List<Activity> activities, ActivityListener listener) CreateListener()
    {
        var activities = new List<Activity>();
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

        await engine.AskAsync("Where is the Eiffel Tower?", sources,
            cancellationToken: TestContext.Current.CancellationToken);

        var span = activities.FirstOrDefault(a => string.Equals(a.OperationName, "ragnet.ask", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.Equal("0", span.GetTagItem("source.count")?.ToString());
        Assert.NotNull(span.GetTagItem("synthesis.strategy"));
    }
}
