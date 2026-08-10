using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Diagnostics.Internal;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// What <c>AddRagDiagnostics</c> wires, and the two places the obvious registration would be wrong.
/// </summary>
/// <remarks>
/// In the <see cref="ActivityCollection"/> because resolving <c>ITraceCollector</c> subscribes a
/// process-wide <c>ActivityListener</c> — which is the point of one of these tests.
/// </remarks>
[Collection(ActivityCollection.Name)]
public sealed class RagDiagnosticsRegistrationTests
{
    [Fact]
    public void EveryGuardRegistration_IsDecorated_NotJustTheLast()
    {
        var calls = new List<string>();

        using var provider = Build(rag =>
        {
            rag.Services.AddSingleton<IRetrievalGuard>(new RecordingGuard("first", calls));
            rag.Services.AddSingleton<IRetrievalGuard>(new RecordingGuard("second", calls));
            rag.AddRagDiagnostics();
        });

        var guards = provider.GetServices<IRetrievalGuard>().ToList();

        // The property the ConfigureResilience idiom does not have. RetrievalGuardBehavior takes an
        // IEnumerable<IRetrievalGuard> and runs all of them, so decorating only the last registration
        // would trace one guard out of two and report the other as never having fired — a trace that
        // looks complete and answers the question wrongly.
        Assert.Equal(2, guards.Count);
        Assert.All(guards, guard => Assert.IsType<TracingRetrievalGuard>(guard));

        for (var i = 0; i < guards.Count; i++)
            guards[i].Inspect([]);

        // And in the order they were registered: a guard chain is applied in sequence, so decoration
        // reordering it would change what the pipeline does.
        string[] expected = ["first", "second"];
        Assert.Equal(expected, calls, StringComparer.Ordinal);
    }

    [Fact]
    public void EverySanitiserRegistration_IsDecorated()
    {
        using var provider = Build(rag =>
        {
            rag.Services.AddSingleton<IChunkSanitiser>(new PassThroughChunkSanitiser());
            rag.Services.AddSingleton<IChunkSanitiser>(new PassThroughChunkSanitiser());
            rag.Services.AddSingleton<IQuerySanitiser>(new PassThroughQuerySanitiser());
            rag.Services.AddSingleton<IQuerySanitiser>(new PassThroughQuerySanitiser());
            rag.AddRagDiagnostics();
        });

        // Both are consumed as IEnumerable<T> too — ChunkSanitiserBehavior.Sanitisers and
        // QuerySanitiserPipelineDecorator's constructor.
        Assert.All(provider.GetServices<IChunkSanitiser>(), s => Assert.IsType<TracingChunkSanitiser>(s));
        Assert.All(provider.GetServices<IQuerySanitiser>(), s => Assert.IsType<TracingQuerySanitiser>(s));
    }

    [Fact]
    public void WithNothingToDecorate_RegistrationIsANoOpRatherThanAFailure()
    {
        using var provider = Build(rag => rag.AddRagDiagnostics());

        // Copying ConfigureResilience's "found nothing to apply to" exception would have made
        // diagnostics refuse to register on a pipeline with no guards — which is most pipelines, and
        // would have made a debugger depend on the security package being installed.
        Assert.Empty(provider.GetServices<IRetrievalGuard>());
        Assert.Empty(provider.GetServices<IChunkSanitiser>());
        Assert.NotNull(provider.GetService<ITraceStore>());
    }

    [Fact]
    public void TheCollectorTheStoreAndThePromptObserver_AreAllResolvable()
    {
        using var provider = Build(rag => rag.AddRagDiagnostics(o => o.Capacity = 7));

        Assert.NotNull(provider.GetService<ITraceCollector>());
        Assert.IsType<TracePromptObserver>(provider.GetService<IPromptObserver>());
        Assert.Equal(7, provider.GetRequiredService<RagTraceOptions>().Capacity);

        // One buffer behind both, or a trace would commit into a buffer the reader never sees.
        Assert.Same(provider.GetRequiredService<ITraceStore>(), provider.GetRequiredService<TraceRingBuffer>());
    }

    [Fact]
    public void ResolvingTheCollector_SubscribesTheSpanListener()
    {
        using var provider = Build(rag => rag.AddRagDiagnostics());

        // The listener subscribes in its constructor, so a singleton nobody resolves never subscribes
        // and the whole feature is silently inert. Every capture seam resolves ITraceCollector, so
        // that resolution is what has to bring the listener into existence.
        _ = provider.GetRequiredService<ITraceCollector>();

        using var source = new ActivitySource("Rag.NET");
        var activity = source.StartActivity("ragnet.retrieve");
        Assert.NotNull(activity);
        activity.Stop();

        Assert.Single(provider.GetRequiredService<ITraceStore>().Snapshot());
    }

    [Fact]
    public void WithAChatClientAndNoAnswerEngine_ATracedAnswerEngineIsSupplied()
    {
        using var provider = Build(rag =>
        {
            rag.Services.AddSingleton(Substitute.For<IChatClient>());
            rag.AddRagDiagnostics();
        });

        // AddRagNet registers no IAnswerEngine — its RagPipeline factory builds a ChatAnswerEngine
        // itself when it finds none — so in the commonest setup of all there is nothing to decorate,
        // and answer capture would have been missing from exactly the configuration most people have.
        Assert.IsType<DiagnosticsAnswerEngineDecorator>(provider.GetService<IAnswerEngine>());
    }

    [Fact]
    public async Task AnAlreadyRegisteredAnswerEngine_IsWrappedRatherThanReplaced()
    {
        var inner = Substitute.For<IAnswerEngine>();
        inner.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new RagResponse { Answer = "from the inner engine", Sources = [] });

        using var provider = Build(rag =>
        {
            rag.Services.AddSingleton(inner);
            rag.AddRagDiagnostics();
        });

        var engine = Assert.IsType<DiagnosticsAnswerEngineDecorator>(provider.GetService<IAnswerEngine>());

        // Wrapped, not swapped: whatever the security package or the user registered still generates
        // the answer, and the decorator only observes it.
        var response = await engine.AskAsync("q", [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("from the inner engine", response.Answer);
    }

    [Fact]
    public void WithNoChatClient_NoAnswerEngineIsInvented()
    {
        using var provider = Build(rag => rag.AddRagDiagnostics());

        // A retrieval-only pipeline has no answer engine on purpose. Registering one whose factory
        // calls ChatAnswerEngine.CreateFromServices would make RagPipeline throw the moment it is
        // resolved — registering diagnostics must never break a pipeline that worked without it.
        Assert.Null(provider.GetService<IAnswerEngine>());
    }

    [Fact]
    public void WithoutAddRagNet_RegistrationSaysSo()
    {
        var builder = new RagBuilder(new ServiceCollection());

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddRagDiagnostics());

        Assert.Contains("AddRagNet", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Builds a provider over a configured <c>AddRagNet</c> container.</summary>
    /// <param name="configure">What to register on the RAG builder.</param>
    /// <returns>The provider; dispose it to unsubscribe anything diagnostics subscribed.</returns>
    private static ServiceProvider Build(Action<RagBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddRagNet(configure);

        return services.BuildServiceProvider();
    }

    private sealed class RecordingGuard(string name, List<string> calls) : IRetrievalGuard
    {
        public IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results)
        {
            calls.Add(name);

            return results;
        }
    }

    private sealed class PassThroughChunkSanitiser : IChunkSanitiser
    {
        public string Sanitise(string text, IReadOnlyDictionary<string, MetadataValue> metadata) => text;
    }

    private sealed class PassThroughQuerySanitiser : IQuerySanitiser
    {
        public string Sanitise(string query) => query;
    }
}
