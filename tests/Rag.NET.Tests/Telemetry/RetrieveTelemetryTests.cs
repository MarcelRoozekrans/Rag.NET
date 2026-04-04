using System.Diagnostics;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Telemetry;
using Xunit;

namespace Rag.NET.Tests.Telemetry;

public class RetrieveTelemetryTests
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

    private static PipelineRetriever CreateSut(
        Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>? pipeline = null) =>
        new()
        {
            Pipeline = pipeline ?? new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
                (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([])),
        };

    [Fact]
    public async Task RetrieveAsync_EmitsRetrieveSpan()
    {
        var (activities, listener) = CreateListener();
        using var _ = listener;

        var retriever = CreateSut();

        var __ = await retriever.RetrieveAsync("what is RAG?", cancellationToken: TestContext.Current.CancellationToken);

        var span = activities.FirstOrDefault(a => string.Equals(a.OperationName, "ragnet.retrieve", StringComparison.Ordinal));
        Assert.NotNull(span);
        Assert.NotNull(span.GetTagItem("query.hash")); // 8-char hex SHA-256 prefix — don't assert exact value
        Assert.NotNull(span.GetTagItem("top_k"));
        Assert.Equal("0", span.GetTagItem("result.count")?.ToString()); // empty result from fake store
    }
}
