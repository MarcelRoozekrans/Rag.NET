using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Benchmarks.Quality.IntegrationTests;
using Rag.NET.Raptor;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// <see cref="RaptorRun"/> is the RAPTOR equivalent of <c>GraphRagRun</c>, and its one critical
/// behaviour is the thing this class exists to pin: at the shipped
/// <see cref="RaptorOptions.CorpusGrowthThreshold"/> of 0.10, ingesting 609 MultiHop-RAG articles
/// would trigger 48 whole-corpus rebuilds, each re-clustering every leaf so far and summarising it.
/// A benchmark must ingest with that debounce suppressed and rebuild once at the end — exactly what
/// <c>RaptorTreeRebuilder</c> documents itself for.
/// <para>
/// <b>Fast tier: no model, no corpus.</b> The chat client and the embedder are both fakes — the
/// expensive, non-deterministic dependencies a real run needs. The leaf store is not: an in-memory
/// SQLite database is neither expensive nor non-deterministic, and <c>SqliteRaptorLeafStoreTests</c>
/// already runs one on every push, so faking it here would test a fake instead of the real store
/// <see cref="RaptorRun"/> uses.
/// </para>
/// <para>
/// <b>Every embedding is derived from the text it embeds, not handed in.</b> <see cref="RaptorRun"/>
/// always calls its embedder — for the leaves it chunks itself and for every summary
/// <c>RaptorIngestionBehavior</c> asks for — so a fake that returned the same vector regardless of
/// input, or that reseeded its randomness on every call, would collapse every chunk to (near-)one
/// point and BIC would correctly find one cluster: no tree. <see cref="FakeDocuments"/> writes a
/// <c>topicN</c> marker into every chunk's text, the fake chat client echoes prompts back rather
/// than returning a constant (so a cluster summary still carries its children's markers), and
/// <see cref="BuildEmbedding"/> reads those markers into three well-separated positions with one
/// <see cref="Random"/> shared — never reseeded — across every call. This is the same scheme
/// <c>RaptorTestContext</c> in <c>Rag.NET.Raptor.Tests</c> uses and for the same reason: a per-call
/// reseed there once made every vector identical and hid two shipped defects.
/// </para>
/// </summary>
public sealed class RaptorRunTests : IDisposable
{
    /// <summary>
    /// Characters per fake chunk's filler, chosen so no two chunks pack into one under
    /// <c>ChunkingOptions.MaxChunkSize</c> (512, the default <see cref="RaptorRun"/> chunks with):
    /// two chunks at this size sum to over 512 (400 + 2 + 400 = 802), so
    /// <c>RecursiveChunkingStrategy</c> can never combine them, while one alone comfortably fits.
    /// </summary>
    private const int FillerLength = 400;

    private static readonly Regex TopicMarker =
        new(@"topic(?<topic>\d+)", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

    private readonly string _cacheRoot;

    public RaptorRunTests()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), "ragnet-raptor-run-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_cacheRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_BuildsTheTreeOnce_NotOncePerGrowthThreshold()
    {
        // At the shipped CorpusGrowthThreshold of 0.10, ingesting 609 articles would trigger 48
        // whole-corpus rebuilds, each re-clustering every leaf so far and summarising it. A
        // benchmark must ingest with the debounce suppressed and rebuild once at the end —
        // exactly what RaptorTreeRebuilder documents itself for.
        var documents = FakeDocuments(count: 40, chunksEach: 3);

        await using var run = await BuildRunAsync(documents, RaptorTreeScope.Corpus);

        Assert.Equal(1, run.CorpusRebuildCount);
        Assert.True(run.SummaryCount > 0, "the rebuild must actually produce a tree");
    }

    [Fact]
    public async Task BuildAsync_PerDocumentScope_BuildsDuringIngestionAndNeverRebuilds()
    {
        var documents = FakeDocuments(count: 40, chunksEach: 8); // 8 clears MinChunksForRaptor's 5

        await using var run = await BuildRunAsync(documents, RaptorTreeScope.PerDocument);

        Assert.Equal(0, run.CorpusRebuildCount);
        Assert.True(run.SummaryCount > 0, "per-document trees are built during ingestion");
    }

    /// <summary>
    /// Wires a fake <see cref="IChatClient"/> that echoes its prompt back as the "summary" (so a
    /// cluster's topic markers survive into the next level) and a fake embedder that derives a
    /// well-separated vector from whatever <c>topicN</c> markers the text carries, then builds a
    /// <see cref="RaptorRun"/> over <paramref name="documents"/>.
    /// </summary>
    private async Task<RaptorRun> BuildRunAsync(
        IReadOnlyList<BeirDocument> documents, RaptorTreeScope scope)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var text = string.Join(" ", callInfo.Arg<IEnumerable<ChatMessage>>().Select(m => m.Text));
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
            });

        // Captured once, outside the per-call callback: a per-call `new Random(...)` here would
        // reseed identically on every embedding request, collapsing every chunk (and every
        // summary at every level) to the same noise regardless of which text was embedded — see
        // the type remarks.
        var rng = new Random(Seed: 1337);
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>().ToList();
                var generated = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var text in texts)
                {
                    generated.Add(new Embedding<float>(BuildEmbedding(text, dims: 8, rng)));
                }

                return Task.FromResult(generated);
            });

        var cache = new EmbeddingCache(_cacheRoot, "raptor-run-tests@fake");

        return await RaptorRun.BuildAsync(
            documents,
            scope,
            embedder,
            cache,
            chatClient,
            leafStorePath: ":memory:",
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Builds <paramref name="count"/> documents of <paramref name="chunksEach"/> chunks apiece.
    /// Each document is <paramref name="chunksEach"/> paragraphs, one per <c>topicN</c> (cycling
    /// through three), separated by blank lines and padded to <see cref="FillerLength"/> — long
    /// enough that <c>RecursiveChunkingStrategy</c> yields exactly one chunk per paragraph and
    /// never packs two together.
    /// </summary>
    private static IReadOnlyList<BeirDocument> FakeDocuments(int count, int chunksEach)
    {
        var documents = new List<BeirDocument>(count);
        for (var i = 0; i < count; i++)
        {
            var text = BuildDocumentText(i, chunksEach);
            documents.Add(new BeirDocument(
                Id: $"doc-{i}", Title: string.Empty, Text: text, RetrievalText: text));
        }

        return documents;
    }

    private static string BuildDocumentText(int documentIndex, int chunksEach)
    {
        var paragraphs = new string[chunksEach];
        for (var i = 0; i < chunksEach; i++)
        {
            var filler = new string('x', FillerLength);
            paragraphs[i] = FormattableString.Invariant(
                $"doc{documentIndex} paragraph{i} topic{i % 3} {filler}");
        }

        return string.Join("\n\n", paragraphs);
    }

    /// <summary>
    /// Derives a vector from every <c>topicN</c> marker <paramref name="text"/> carries: three
    /// well-separated positions (topics 0 and 1 close together, topic 2 far away — a genuine,
    /// non-degenerate hierarchy for BIC-selected k to find), plus noise from
    /// <paramref name="rng"/>. Text with no marker — nothing this test ever embeds, but a safe
    /// fallback — gets unstructured noise instead.
    /// </summary>
    private static float[] BuildEmbedding(string text, int dims, Random rng)
    {
        var matches = TopicMarker.Matches(text);
        if (matches.Count == 0)
        {
            return Enumerable.Range(0, dims).Select(_ => (float)rng.NextDouble()).ToArray();
        }

        var positions = matches
            .Select(m => PositionForTopic(int.Parse(m.Groups["topic"].Value, CultureInfo.InvariantCulture)))
            .Distinct()
            .ToList();
        var center = positions.Average();

        return Enumerable.Range(0, dims).Select(_ => (float)(center + (rng.NextDouble() * 0.05))).ToArray();
    }

    private static double PositionForTopic(int topic) => topic switch
    {
        0 => 0.0,
        1 => 0.3,
        _ => 10.0,
    };
}
