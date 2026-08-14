using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.DependencyInjection;
using Rag.NET.Graph;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// The relationship weight the LLM returns is data the LLM controls, and issue #209 is what
/// happened when it reached the clusterer unchecked.
/// <para>
/// <b>The failure was not a crash and not a wrong number in a report — it was the entire graph.</b>
/// <see cref="GraphRagOptions.EntityExtractionPrompt"/> asks for <c>"weight": 1.0</c>; over the full
/// 609-article MultiHop-RAG corpus the model answered two of them with acquisition prices,
/// <c>Microsoft -&gt; Mojang</c> at 2.5e9 and <c>Microsoft -&gt; Rare</c> at 3.75e8. Those two edges
/// out of 147,021 carried 99.99% of the graph's total weight, modularity's null-model penalty for an
/// ordinary pair collapsed to about 1e-9, and Leiden put <b>57,484 of 62,392 entities — 92.13% — in
/// one community</b> at modularity 0.0001. Rebuilding the same cached extractions with the bound
/// asserted here alters 20 of those 147,021 weights and brings it to 5,629 entities, 9.02%, at
/// modularity 0.7496.
/// </para>
/// <para>
/// <b>Asserted against <c>ConvertRelationships</c> directly, and that is not test convenience.</b>
/// <see cref="double.NaN"/> is unreachable through the behaviour's public surface — no JSON number
/// can express it and <c>System.Text.Json</c> rejects the named literal — while
/// <see cref="GraphRelationship.Weight"/> is a public init property that accepts it happily. A test
/// that could only go through the deserializer would leave the one input that poisons every
/// comparison it touches untested. Infinity, by contrast, <i>is</i> reachable and is exercised both
/// ways: <c>JsonDocument</c> parses the JSON number <c>1e999</c> to <c>+∞</c> without complaint,
/// which is the shape a model padding a number with zeroes produces.
/// </para>
/// </summary>
/// <remarks>
/// In the telemetry collection because the "Rag.NET" <see cref="ActivitySource"/> is process-global:
/// a listener attached here sees every span any concurrently running class emits, and the assertion
/// below wants this run's.
/// </remarks>
[Collection("Telemetry")]
public sealed class RelationshipWeightBoundTests : IAsyncDisposable
{
    private const string DocumentId = "weight-bound-doc";

    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly SqliteGraphStore _graphStore = new(":memory:");

    public ValueTask DisposeAsync() => _graphStore.DisposeAsync();

    /// <summary>
    /// The four inputs issue #209 names, each against what must survive it. The first is the
    /// measured Mojang edge; the rest are the boundary its neighbours sit on.
    /// </summary>
    [Theory]
    [InlineData(2.5e9, 10.0)]              // the measured defect: an acquisition price as a weight
    [InlineData(3.75e8, 10.0)]             // its smaller sibling, Microsoft -> Rare
    [InlineData(-1.0, 1.0)]                // the minimum measured over the real corpus
    [InlineData(0.0, 1.0)]                 // not an edge at all
    [InlineData(double.NaN, 1.0)]          // poisons every comparison downstream
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 1.0)]
    [InlineData(10.0, 10.0)]               // exactly at the ceiling: kept
    [InlineData(3.5, 3.5)]                 // below it: ordering preserved, which is the point
    [InlineData(1.0, 1.0)]                 // what the schema asks for
    public void ConvertRelationships_BoundsTheWeightTheModelReturned(double returned, double stored)
    {
        var audit = new RelationshipWeightAudit();

        var converted = GraphEntityExtractionBehavior.ConvertRelationships(
            [Relationship(returned)], new GraphRagOptions(), DocumentId, audit);

        var only = Assert.Single(converted);
        Assert.Equal(stored, only.Weight);
        Assert.True(
            double.IsFinite(only.Weight) && only.Weight > 0.0,
            FormattableString.Invariant(
                $"A weight of {returned} reached the graph as {only.Weight}, which Leiden cannot use."));
    }

    /// <summary>
    /// A bounded weight keeps its edge. Dropping the relationship would cost the graph endpoints
    /// and a description that were never in question, on top of the 3.7% of relationships
    /// <c>Leiden.BuildAdjacency</c> already discards for naming reasons.
    /// </summary>
    [Fact]
    public void ConvertRelationships_KeepsTheEdgeWhoseWeightWasUnusable()
    {
        var audit = new RelationshipWeightAudit();

        var converted = GraphEntityExtractionBehavior.ConvertRelationships(
            [Relationship(double.NaN), Relationship(2.5e9), Relationship(1.0)],
            new GraphRagOptions(),
            DocumentId,
            audit);

        Assert.Equal(3, converted.Count);
        Assert.Equal("Microsoft", converted[0].SourceEntity);
        Assert.Equal("acquired", converted[0].Description);
    }

    /// <summary>Every alteration is counted, because the defect's whole cost was being silent.</summary>
    [Fact]
    public void ConvertRelationships_CountsWhatItAltered_AndKeepsTheLargestWeightSeen()
    {
        var audit = new RelationshipWeightAudit();

        _ = GraphEntityExtractionBehavior.ConvertRelationships(
            [Relationship(2.5e9), Relationship(3.75e8), Relationship(-1.0), Relationship(double.NaN), Relationship(1.0)],
            new GraphRagOptions(),
            DocumentId,
            audit);

        Assert.Equal(2, audit.Clamped);
        Assert.Equal(2, audit.Replaced);
        Assert.Equal(2.5e9, audit.LargestWeight);
        Assert.True(audit.Altered);
    }

    /// <summary>A corpus that needs no bounding says so positively rather than by silence.</summary>
    [Fact]
    public void ConvertRelationships_WhenEveryWeightIsUsable_RecordsNoAlteration()
    {
        var audit = new RelationshipWeightAudit();

        _ = GraphEntityExtractionBehavior.ConvertRelationships(
            [Relationship(1.0), Relationship(2.0)], new GraphRagOptions(), DocumentId, audit);

        Assert.Equal(0, audit.Clamped);
        Assert.Equal(0, audit.Replaced);
        Assert.Equal(2.0, audit.LargestWeight);
        Assert.False(audit.Altered);
    }

    /// <summary>
    /// The ceiling is read, not merely registered — the shape of defect #190 and the dead-settings
    /// audit #108, asserted in both directions: a different ceiling stores a different weight.
    /// </summary>
    [Theory]
    [InlineData(3.0, 3.0)]
    [InlineData(1_000_000.0, 1_000_000.0)]
    public void ConvertRelationships_ClampsToTheConfiguredCeiling(double ceiling, double stored)
    {
        var audit = new RelationshipWeightAudit();

        var converted = GraphEntityExtractionBehavior.ConvertRelationships(
            [Relationship(2.5e9)],
            new GraphRagOptions { MaxRelationshipWeight = ceiling },
            DocumentId,
            audit);

        Assert.Equal(stored, converted[0].Weight);
    }

    /// <summary>
    /// The end-to-end path, over the JSON shape a model actually produces. <c>1e999</c> is a legal
    /// JSON number that parses to <c>+∞</c> silently, which is how an infinity gets into a graph
    /// without anything throwing.
    /// </summary>
    [Theory]
    [InlineData("2500000000.0", 10.0)]
    [InlineData("1e999", 1.0)]
    [InlineData("-1.0", 1.0)]
    public async Task Extraction_BoundsTheWeightBeforeItReachesTheGraphStore(string literal, double stored)
    {
        var sut = new GraphEntityExtractionBehavior(
            _chatClient, _embedder, _graphStore, new GraphRagOptions { GleaningPasses = 0 });

        SetupChatClient(PayloadWithWeight(literal));
        SetupEmbedder();

        await sut.HandleAsync(CreateContext(), TestContext.Current.CancellationToken, Terminal);

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);
        var only = Assert.Single(snapshot.Relationships);
        Assert.Equal(stored, only.Weight);
    }

    /// <summary>What was altered is on the span, whether or not anything was.</summary>
    [Fact]
    public async Task Extraction_TagsTheExtractSpanWithWhatItAltered()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var sut = new GraphEntityExtractionBehavior(
            _chatClient, _embedder, _graphStore, new GraphRagOptions { GleaningPasses = 0 });

        SetupChatClient(PayloadWithWeight("2500000000.0"));
        SetupEmbedder();

        await sut.HandleAsync(CreateContext(), TestContext.Current.CancellationToken, Terminal);

        var span = activities.Single(a =>
            string.Equals(a.OperationName, "ragnet.graphrag.extract", StringComparison.Ordinal)
            && Equals(a.GetTagItem("document.id"), DocumentId));
        Assert.Equal(1, span.GetTagItem("graphrag.relationship.weight.clamped"));
        Assert.Equal(0, span.GetTagItem("graphrag.relationship.weight.replaced"));
        Assert.Equal(2.5e9, span.GetTagItem("graphrag.relationship.weight.largest"));
    }

    /// <summary>
    /// A ceiling that cannot bound anything fails at the configuring line rather than producing a
    /// graph nobody would question. Infinity type-checks and reinstates the defect; NaN disables the
    /// bound because <c>weight &gt; NaN</c> is false for every weight.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnusableWeightCeiling_ThrowsAtRegistration(double ceiling)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ConfiguredRagBuilder.Create().UseGraphRag(o => o.MaxRelationshipWeight = ceiling));

        Assert.Contains("MaxRelationshipWeight", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The default is the measured one, and it registers.</summary>
    [Fact]
    public void DefaultWeightCeiling_IsTenAndRegisters()
    {
        var builder = ConfiguredRagBuilder.Create();

        builder.UseGraphRag();

        var options = builder.Services.BuildServiceProvider().GetRequiredService<GraphRagOptions>();
        Assert.Equal(10.0, options.MaxRelationshipWeight);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ExtractedRelationship Relationship(double weight) =>
        new() { Source = "Microsoft", Target = "Mojang", Description = "acquired", Weight = weight };

    private static string PayloadWithWeight(string literal) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            {"entities":[{"name":"Microsoft","type":"Organization","description":"Tech company"}],"relationships":[{"source":"Microsoft","target":"Mojang","description":"acquired","weight":{{literal}}}]}
            """);

    private static IngestionContext CreateContext()
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId(DocumentId),
                FileName = "test.txt",
            },
            GetNextBm25DocId = () => 0,
        };

        ctx.Chunks.Add(new TextChunk
        {
            Text = "Microsoft acquired Mojang for 2.5 billion dollars.",
            DocumentId = new DocumentId(DocumentId),
            ChunkIndex = 0,
        });

        return ctx;
    }

    private static ValueTask<IngestionResult> Terminal(IngestionContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(new IngestionResult
        {
            DocumentId = ctx.Metadata.DocumentId,
            ChunksStored = 0,
        });

    private void SetupChatClient(string response) =>
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

    private void SetupEmbedder() =>
        _embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                new(call.Arg<IEnumerable<string>>()!
                    .Select(_ => new Embedding<float>(new float[] { 0.1f, 0.2f }))
                    .ToList())));
}
