using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Raptor;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// <see cref="RaptorRun"/> is the RAPTOR equivalent of <see cref="GraphRagRun"/>, and its one
/// critical behaviour is the thing this class exists to pin: ingesting a growing corpus under
/// <c>RaptorTreeScope.Corpus</c> must not re-cluster and re-summarise everything so far once per
/// some threshold — it must produce exactly one tree, from the single explicit
/// <c>RaptorTreeRebuilder.RebuildAsync</c> call made after ingestion finishes.
/// <para>
/// <b>No model, no downloaded corpus, no LLM.</b> This lives beside <see cref="GraphRagRun"/> in
/// the same project rather than in the fast-tier <c>Rag.NET.Benchmarks.Quality.Tests</c> project:
/// <c>ci.yml</c> gates its fast tier on <c>RequiresDocker</c>, not <c>RequiresSecrets</c>
/// (<c>RequiresSecrets</c> is an overlay, not a fourth tier), and this project declares neither, so
/// it already runs on every push in well under a second — the same tier
/// <c>RecordingEmbeddingGeneratorTests</c> and <c>HydeAblationRowTests</c> already occupy for
/// exactly this reason. Moving this file into the other project bought nothing and cost a
/// cross-test-project reference nowhere else in the repository has.
/// </para>
/// <para>
/// <b>The chat client and the embedder are fakes; the leaf store is not.</b> An in-memory SQLite
/// database is neither expensive nor non-deterministic, and <c>SqliteRaptorLeafStoreTests</c>
/// already runs one on every push, so faking it here would test a fake instead of the real store
/// <see cref="RaptorRun"/> uses. <see cref="EchoChatClient"/> and <see cref="TopicAwareEmbedder"/>
/// are hand-rolled rather than built with a mocking library, the same way
/// <see cref="PromptEchoChatClient"/> is — nothing in this project references one.
/// </para>
/// <para>
/// <b>Every embedding is derived from the text it embeds, not handed in.</b> <see cref="RaptorRun"/>
/// always calls its embedder — for the leaves it chunks itself and for every summary
/// <c>RaptorIngestionBehavior</c> asks for under <c>PerDocument</c> scope — so a fake that returned
/// the same vector regardless of input, or that reseeded its randomness on every call, would
/// collapse every chunk to (near-)one point and BIC would correctly find one cluster: no tree.
/// <see cref="FakeDocuments"/> writes a <c>topicN</c> marker into every chunk's text,
/// <see cref="EchoChatClient"/> echoes prompts back in full rather than returning a constant or a
/// bounded head of it (so a cluster summary still carries every child's marker, unlike
/// <see cref="PromptEchoChatClient"/>'s 2,000-character bound, which a wide cluster of these small
/// chunks could run past), and <see cref="TopicAwareEmbedder"/> reads those markers into three
/// well-separated positions with one <see cref="Random"/> shared — never reseeded — across every
/// call. This is the same scheme <c>RaptorTestContext</c> in <c>Rag.NET.Raptor.Tests</c> uses and
/// for the same reason: a per-call reseed there once made every vector identical and hid two
/// shipped defects.
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

    /// <summary>
    /// A generous ceiling on how many cluster summaries one corpus-scope rebuild over 120 leaves
    /// (40 documents of 3 chunks each) should ever need. The number that matters is the contrast: a
    /// per-document rebuild loop over the same 40 documents would need on the order of 200 calls
    /// (one build every few documents once the corpus outgrows the debounce's baseline), and this
    /// bound sits an order of magnitude below that — see the mutation-tested regression in the
    /// implementation report for the actual counts observed on both sides of the fix this pins.
    /// </summary>
    private const long MaxPlausibleSummariserCallsForOneRebuild = 20;

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
        // A benchmark ingesting a growing corpus under Corpus scope must produce exactly one tree
        // — the single explicit rebuild after ingestion — never one per document as the corpus
        // crosses some threshold. CorpusRebuildCount alone cannot show that: RaptorRun sets it to 1
        // beside the single RebuildAsync call by construction, so it reads 1 whether or not
        // ingestion also rebuilt along the way. SummariserCalls is the number that actually moves:
        // it counts every request RaptorIngestionBehavior sent the summariser, including any that
        // happened mid-ingestion, and LeafCount independently confirms every document's chunks
        // actually made it into the corpus this run measured.
        var documents = FakeDocuments(count: 40, chunksEach: 3);

        await using var run = await BuildRunAsync(documents, RaptorTreeScope.Corpus);

        Assert.Equal(1, run.CorpusRebuildCount);
        Assert.Equal(120, run.LeafCount);
        Assert.True(
            run.SummariserCalls < MaxPlausibleSummariserCallsForOneRebuild,
            $"expected one rebuild's worth of summariser calls (< {MaxPlausibleSummariserCallsForOneRebuild}), " +
            $"got {run.SummariserCalls} — ingestion summarised along the way, not just the final rebuild");
        Assert.True(run.SummaryCount > 0, "the rebuild must actually produce a tree");
    }

    [Fact]
    public async Task BuildAsync_PerDocumentScope_BuildsDuringIngestionAndNeverRebuilds()
    {
        var documents = FakeDocuments(count: 40, chunksEach: 8); // 8 clears MinChunksForRaptor's 5

        await using var run = await BuildRunAsync(documents, RaptorTreeScope.PerDocument);

        Assert.Equal(0, run.CorpusRebuildCount);
        Assert.Equal(320, run.LeafCount);
        Assert.True(run.SummaryCount > 0, "per-document trees are built during ingestion");
    }

    /// <summary>
    /// Wires a fake <see cref="IChatClient"/> that echoes its prompt back in full as the "summary"
    /// (so a cluster's topic markers survive into the next level) and a fake embedder that derives a
    /// well-separated vector from whatever <c>topicN</c> markers the text carries, then builds a
    /// <see cref="RaptorRun"/> over <paramref name="documents"/>.
    /// </summary>
    private async Task<RaptorRun> BuildRunAsync(
        IReadOnlyList<BeirDocument> documents, RaptorTreeScope scope)
    {
        var chatClient = new EchoChatClient();

        // Captured once, outside the per-call embedder: a per-call `new Random(...)` here would
        // reseed identically on every embedding request, collapsing every chunk (and every summary
        // at every level) to the same noise regardless of which text was embedded — see the type
        // remarks.
        var rng = new Random(Seed: 1337);
        var embedder = new TopicAwareEmbedder(rng);

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
    /// enough that <c>RecursiveChunkingStrategy</c> yields exactly one chunk per paragraph and never
    /// packs two together.
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

    /// <summary>
    /// A chat client that answers every request with the whole prompt it was given, unbounded
    /// (unlike <see cref="PromptEchoChatClient"/>, which is right for its own callers but would
    /// truncate a wide cluster's concatenated children here before every marker got through — see
    /// the type remarks).
    /// </summary>
    private sealed class EchoChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            var text = string.Join(" ", messages.Select(m => m.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("RaptorIngestionBehavior does not stream summaries.");

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// An embedder that derives a vector from a text's <c>topicN</c> markers via
    /// <see cref="BuildEmbedding"/>, sharing one <see cref="Random"/> across every call.
    /// </summary>
    private sealed class TopicAwareEmbedder(Random rng) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(values);

            var generated = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var text in values)
            {
                generated.Add(new Embedding<float>(BuildEmbedding(text, dims: 8, rng)));
            }

            return Task.FromResult(generated);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
