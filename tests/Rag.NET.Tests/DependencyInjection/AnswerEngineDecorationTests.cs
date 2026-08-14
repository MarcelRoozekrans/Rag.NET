using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// The seam that made answer auditing and answer tracing order-independent (issue #195):
/// decorations are applied when the pipeline composes its engine, not when they are registered.
/// </summary>
public sealed class AnswerEngineDecorationTests
{
    [Fact]
    public void ADecorationAddedBeforeTheEngine_StillWrapsIt()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.Services
            .RagAnswerEngineDecorations(nameof(ADecorationAddedBeforeTheEngine_StillWrapsIt))
            .Add("outer", static (inner, _) => new RecordingEngine(inner)));

        // The registration the decoration has to survive: an answer engine — or the chat client
        // AddRagNet builds one from — arriving after AddRagNet has already returned.
        services.AddSingleton(EngineReturning("from the engine"));

        using var provider = services.BuildServiceProvider();
        var composed = Assert.IsType<RecordingEngine>(provider.GetRequiredService<ComposedAnswerEngine>().Engine);

        Assert.IsAssignableFrom<IAnswerEngine>(composed.Inner);
    }

    [Fact]
    public async Task DecorationsApply_OutwardsInTheOrderTheyWereAdded()
    {
        var order = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(EngineReturning("answer"));
        services.AddRagNet(rag =>
        {
            var decorations = rag.Services.RagAnswerEngineDecorations(nameof(DecorationsApply_OutwardsInTheOrderTheyWereAdded));
            decorations.Add("first", (inner, _) => new RecordingEngine(inner, order, "first"));
            decorations.Add("second", (inner, _) => new RecordingEngine(inner, order, "second"));
        });

        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<ComposedAnswerEngine>().Engine!;
        _ = await engine.AskAsync("q", [], cancellationToken: TestContext.Current.CancellationToken);

        // Added first is innermost, so the one added last observes the call first — the same
        // outward-stacking rule the retrieval pipeline and the client decorators follow.
        string[] expected = ["second", "first"];
        Assert.Equal(expected, order, StringComparer.Ordinal);
    }

    /// <summary>
    /// A layered composition root that reaches <c>UseAuditLog</c> twice must audit each answer
    /// once, not twice — the first-wins convention the idempotent <c>Use*</c> extensions carry.
    /// </summary>
    [Fact]
    public async Task ARepeatedKey_IsAppliedOnce()
    {
        var order = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(EngineReturning("answer"));
        services.AddRagNet(rag =>
        {
            var decorations = rag.Services.RagAnswerEngineDecorations(nameof(ARepeatedKey_IsAppliedOnce));
            decorations.Add("audit", (inner, _) => new RecordingEngine(inner, order, "audit"));
            decorations.Add("audit", (inner, _) => new RecordingEngine(inner, order, "audit"));
        });

        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<ComposedAnswerEngine>().Engine!;
        _ = await engine.AskAsync("q", [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["audit"], order, StringComparer.Ordinal);
    }

    [Fact]
    public void WithoutAddRagNet_TheAccessorSaysWhichCallIsInTheWrongPlace()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.RagAnswerEngineDecorations("UseSomething"));

        Assert.Contains("UseSomething", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddRagNet", exception.Message, StringComparison.Ordinal);
    }

    private static IAnswerEngine EngineReturning(string answer)
    {
        var engine = Substitute.For<IAnswerEngine>();
        engine.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new RagResponse { Answer = answer, Sources = [] });

        return engine;
    }

    private sealed class RecordingEngine(IAnswerEngine inner, List<string>? calls = null, string? name = null)
        : IAnswerEngine
    {
        public IAnswerEngine Inner => inner;

        public Task<RagResponse> AskAsync(
            string query,
            IReadOnlyList<SearchResult> sources,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (calls is not null && name is not null)
                calls.Add(name);

            return inner.AskAsync(query, sources, options, cancellationToken);
        }

        public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
            string query,
            IReadOnlyList<SearchResult> sources,
            RagOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.AskStreamingAsync(query, sources, options, cancellationToken);
    }
}
