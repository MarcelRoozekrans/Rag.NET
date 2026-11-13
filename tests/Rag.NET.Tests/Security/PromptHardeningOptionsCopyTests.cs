using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Security;
using Rag.NET.Tests.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Security;

/// <summary>
/// The hardening decorator copies the caller's <see cref="RagOptions"/> to add its system message.
/// The copy must carry every other property and the record of which retrieval fields were
/// assigned (issue #945). These tests live here because that record is internal to Abstractions.
/// </summary>
public class PromptHardeningOptionsCopyTests
{
    public static TheoryData<string> PropertyNamesExceptHistory()
    {
        var data = new TheoryData<string>();
        foreach (var property in RagOptionsSamples.SettableProperties)
        {
            if (!string.Equals(property.Name, nameof(RagOptions.ConversationHistory), StringComparison.Ordinal))
                data.Add(property.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PropertyNamesExceptHistory))]
    public async Task AskAsync_PassesPropertyThrough(string name)
    {
        var property = RagOptionsSamples.Property(name);
        var source = RagOptionsSamples.AllSet();

        var captured = await CaptureAskAsync(source);

        Assert.Equal(property.GetValue(source), property.GetValue(captured));
    }

    [Theory]
    [MemberData(nameof(PropertyNamesExceptHistory))]
    public async Task AskStreamingAsync_PassesPropertyThrough(string name)
    {
        var property = RagOptionsSamples.Property(name);
        var source = RagOptionsSamples.AllSet();

        var captured = await CaptureAskStreamingAsync(source);

        Assert.Equal(property.GetValue(source), property.GetValue(captured));
    }

    [Fact]
    public async Task AskAsync_WithRetrievalOnly_AssignsNoShortFormField()
    {
        var captured = await CaptureAskAsync(new RagOptions { Retrieval = new RetrievalOptions() });

        Assert.Empty(captured.AssignedRetrievalFields);
    }

    [Fact]
    public async Task AskAsync_KeepsAssignedShortFormFields()
    {
        var captured = await CaptureAskAsync(new RagOptions { TopK = 5, MetadataFilter = null });

        Assert.Equal([nameof(RagOptions.TopK), nameof(RagOptions.MetadataFilter)], captured.AssignedRetrievalFields);
    }

    [Fact]
    public async Task AskAsync_LeavesTheCallersOptionsUnchanged()
    {
        var history = new List<ChatMessage> { new(ChatRole.User, "earlier") };
        var source = new RagOptions { ConversationHistory = history };

        var captured = await CaptureAskAsync(source);

        Assert.NotSame(source, captured);
        Assert.Same(history, source.ConversationHistory);
        Assert.Single(history);
        Assert.Equal(2, captured.ConversationHistory!.Count);
    }

    private static PromptHardeningAnswerEngineDecorator Decorate(IAnswerEngine inner) =>
        new(inner, new PromptHardeningOptions());

    private static async Task<RagOptions> CaptureAskAsync(RagOptions source)
    {
        RagOptions? captured = null;
        var inner = Substitute.For<IAnswerEngine>();
        inner.AskAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<SearchResult>>(),
                Arg.Do<RagOptions?>(o => captured = o),
                Arg.Any<CancellationToken>())
            .Returns(new RagResponse { Answer = "ok", Sources = [] });

        await Decorate(inner).AskAsync("q", [], source, TestContext.Current.CancellationToken);

        return captured!;
    }

    private static async Task<RagOptions> CaptureAskStreamingAsync(RagOptions source)
    {
        RagOptions? captured = null;
        var inner = Substitute.For<IAnswerEngine>();
        inner.AskStreamingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<SearchResult>>(),
                Arg.Do<RagOptions?>(o => captured = o),
                Arg.Any<CancellationToken>())
            .Returns(Empty());

        await foreach (var _ in Decorate(inner).AskStreamingAsync("q", [], source, TestContext.Current.CancellationToken))
        {
        }

        return captured!;
    }

    private static async IAsyncEnumerable<RagStreamingUpdate> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
