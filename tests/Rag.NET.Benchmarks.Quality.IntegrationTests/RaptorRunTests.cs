using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Raptor;
using Rag.NET.Raptor.Store;
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
    /// <remarks>
    /// Silently coupled to <c>SelectClusterCount</c>'s auto-selected <c>maxK = Min(count, 10)</c>
    /// cap (#345): with 120 leaves and a shallow tree, ten clusters per level bounds how many
    /// summaries — and therefore how many summariser calls — one rebuild can produce. If #345's fix
    /// makes <c>k</c> scale with corpus size instead of capping at 10, this bound may need to move
    /// too, and a red run here would read as "ingestion summarised along the way" when the real
    /// cause is a larger, legitimate <c>k</c> for the same fixed leaf count.
    /// </remarks>
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
    public async Task BuildAsync_CapturesEveryLevelsClusterShape_IncludingItsImbalance()
    {
        // The measurement #345's design deferred to measurement. The floor guarantees a level's
        // MEAN cluster size and says outright that it does not bound the maximum, so the only
        // thing that can answer "is the mean enough on a real corpus?" is the largest cluster
        // against that mean. `raptor.cluster.max.size` has carried it since #345 and nothing
        // outside Rag.NET.Raptor.Tests read it, which is why a corpus run could report success
        // and reveal nothing about the margin. Asserted here at 120 leaves so a broken capture
        // fails in seconds rather than 25 minutes into a corpus build.
        var documents = FakeDocuments(count: 40, chunksEach: 3);

        await using var run = await BuildRunAsync(documents, RaptorTreeScope.Corpus);

        var levels = run.Levels;
        Assert.NotEmpty(levels);

        var first = levels[0];
        Assert.Equal(120, first.ChunkCount);
        Assert.True(first.ClusterCount > 0, $"level {first.Level} recorded {first.ClusterCount} clusters");

        // A real size, not a default: at least one chunk, never more than the level itself.
        Assert.InRange(first.MaxClusterSize, 1, first.ChunkCount);

        // The largest cluster cannot be smaller than the mean, so anything below 1.0 means the
        // figure was not read off the span at all.
        Assert.True(
            first.Imbalance >= 1.0,
            $"imbalance {first.Imbalance:F2}x is below an even split, so the tag was not read");
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
    /// <see cref="RaptorRun.IngestLeavesDirectlyAsync"/> is a hand-copy of the field mapping
    /// <see cref="RaptorIngestionBehavior"/>'s own corpus-scope leaf persistence uses —
    /// <c>DocumentId.Value</c>, <c>ChunkIndex</c>, <c>Text</c>, <c>Embedding.ToArray()</c> — and the
    /// whole Corpus-scope bypass <see cref="RaptorRun"/>'s type remarks describe rests on the two
    /// staying identical. This proves it rather than asserting it in prose: the same document,
    /// chunked and embedded once, is fed through both paths, and the leaves each one persists must
    /// match exactly.
    /// </summary>
    [Fact]
    public async Task IngestLeavesDirectlyAsync_MatchesRaptorIngestionBehaviorsOwnLeafPersistence()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = FakeDocuments(count: 1, chunksEach: 5)[0];
        var chunks = await GraphRagSliceIngestion.ChunkAsync(document, ct);
        Assert.True(chunks.Count > 1, "the fixture must chunk into more than one piece to be a real comparison.");

        // A pure function of the chunk text, so both paths embed the SAME vectors independently of
        // call order or count — what is under test is the leaf field mapping, not the embedder.
        var embedder = new DeterministicTextEmbedder();
        var texts = chunks.Select(c => c.Text).ToList();
        var vectors = await embedder.GenerateAsync(texts, cancellationToken: ct);
        var embedded = new List<EmbeddedChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            embedded.Add(new EmbeddedChunk { Chunk = chunks[i], Embedding = vectors[i].Vector });
        }

        var productionLeaves = await PersistLeavesThroughBehaviorAsync(document, embedded, embedder, ct);
        var handCopyLeaves = await PersistLeavesThroughRaptorRunAsync(document, embedder, ct);

        AssertSameLeaves(productionLeaves, handCopyLeaves);
    }

    /// <summary>
    /// Path A — the production implementation: <see cref="RaptorIngestionBehavior"/>'s own
    /// corpus-scope leaf persistence. <c>MaxTreeDepth = 0</c> stops it from clustering anything, so
    /// persisting the leaves it was handed is the only thing this call does.
    /// </summary>
    private static async Task<IReadOnlyList<RaptorLeaf>> PersistLeavesThroughBehaviorAsync(
        BeirDocument document, List<EmbeddedChunk> embedded, DeterministicTextEmbedder embedder, CancellationToken ct)
    {
        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(ct);
        var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus, MaxTreeDepth = 0 };
        var behavior = new RaptorIngestionBehavior(new EchoChatClient(), embedder, options, leafStore);
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId(document.Id), FileName = document.Id },
            GetNextBm25DocId = static () => 0,
        };
        ctx.EmbeddedChunks.AddRange(embedded);
        _ = await behavior.HandleAsync(ctx, ct, static (c, _) => ValueTask.FromResult(
            new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        return await leafStore.GetAllLeavesAsync(ct);
    }

    /// <summary>
    /// Path B — the hand-copy under test: <see cref="RaptorRun.IngestLeavesDirectlyAsync"/>, reached
    /// through <see cref="RaptorRun.BuildAsync"/> over the SAME document with the SAME deterministic
    /// embedder, so it chunks and embeds exactly what <see cref="PersistLeavesThroughBehaviorAsync"/>
    /// built. A file-backed leaf store is needed here (unlike <c>:memory:</c>) so this method can
    /// reopen the same database after the run disposes its own connection.
    /// </summary>
    private async Task<IReadOnlyList<RaptorLeaf>> PersistLeavesThroughRaptorRunAsync(
        BeirDocument document, DeterministicTextEmbedder embedder, CancellationToken ct)
    {
        var storePath = Path.Combine(_cacheRoot, "hand-copy-leaves.db");
        var cache = new EmbeddingCache(_cacheRoot, "raptor-run-tests-leaf-equivalence@fake");
        try
        {
            await using (var run = await RaptorRun.BuildAsync(
                [document], RaptorTreeScope.Corpus, embedder, cache, new EchoChatClient(), storePath, ct))
            {
                // Only the persisted leaves matter here; whatever tree BuildAsync's own rebuild
                // produces over one document's 5 leaves is not this test's concern.
                _ = run;
            }

            await using var leafStore = new SqliteRaptorLeafStore(storePath);
            await leafStore.InitializeAsync(ct);
            return await leafStore.GetAllLeavesAsync(ct);
        }
        finally
        {
            // Microsoft.Data.Sqlite pools connections even past Dispose, which keeps the file
            // handle open at the OS level — harmless normally, but this test's teardown deletes
            // _cacheRoot recursively, and a pooled handle on hand-copy-leaves.db turns that into an
            // IOException. Clearing the pool here, before that teardown runs, is what lets the file
            // actually be gone by the time it tries.
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>Both leaf sets, ordered by chunk index, must agree field for field.</summary>
    private static void AssertSameLeaves(IReadOnlyList<RaptorLeaf> expected, IReadOnlyList<RaptorLeaf> actual)
    {
        var expectedByIndex = expected.OrderBy(l => l.ChunkIndex).ToList();
        var actualByIndex = actual.OrderBy(l => l.ChunkIndex).ToList();

        Assert.Equal(expectedByIndex.Count, actualByIndex.Count);
        for (var i = 0; i < expectedByIndex.Count; i++)
        {
            Assert.Equal(expectedByIndex[i].DocumentId, actualByIndex[i].DocumentId, StringComparer.Ordinal);
            Assert.Equal(expectedByIndex[i].ChunkIndex, actualByIndex[i].ChunkIndex);
            Assert.Equal(expectedByIndex[i].Text, actualByIndex[i].Text, StringComparer.Ordinal);
            Assert.Equal(expectedByIndex[i].Embedding, actualByIndex[i].Embedding);
        }
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

        // Every one of the `dims` dimensions gets the same center plus independent noise, so the
        // fake space this test clusters over is effectively 1-D and any two chunks' vectors are
        // near-collinear (cosine similarity ≈ 1) — correct for RAPTOR's Euclidean GMM here, but a
        // trap if this fake were ever reused for a retrieval-ranking test that reads cosine.
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

    /// <summary>
    /// An embedder whose vector is a pure function of the text handed in — no shared state, no
    /// randomness — so <see cref="IngestLeavesDirectlyAsync_MatchesRaptorIngestionBehaviorsOwnLeafPersistence"/>
    /// can drive two independent code paths over the same text and get back the same embeddings
    /// regardless of call order or count.
    /// </summary>
    private sealed class DeterministicTextEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        private const int Dimensions = 8;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(values);

            var generated = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var text in values)
            {
                generated.Add(new Embedding<float>(VectorFor(text)));
            }

            return Task.FromResult(generated);
        }

        /// <summary>The text's SHA-256 digest, spread across <see cref="Dimensions"/> floats in [0, 1).</summary>
        private static float[] VectorFor(string text)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            var vector = new float[Dimensions];
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = hash[i] / 255f;
            }

            return vector;
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
