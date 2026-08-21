using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Raptor.Tests;

/// <summary>
/// Shared NSubstitute fixtures and context-building helpers for RAPTOR ingestion tests. Promoted
/// out of <see cref="RaptorIngestionBehaviorTests"/> so every later test class exercising
/// <see cref="RaptorIngestionBehavior"/> shares the same setup rather than growing its own copy.
/// </summary>
internal sealed class RaptorTestContext
{
    internal IChatClient ChatClient { get; } = Substitute.For<IChatClient>();

    internal IEmbeddingGenerator<string, Embedding<float>> Embedder { get; } =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    internal IngestionContext CreateContext(int chunkCount, int embeddingDims = 8, string documentId = "test-doc")
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId(documentId), FileName = "test.txt", ContentType = "text/plain" },
            GetNextBm25DocId = () => 0,
        };

        var rng = new Random(SeedFor(documentId));
        for (var i = 0; i < chunkCount; i++)
        {
            var chunk = new TextChunk
            {
                Text = $"Chunk {i} content about topic {i % 3}",
                DocumentId = new DocumentId(documentId),
                ChunkIndex = i,
            };
            var embedding = GenerateEmbedding(rng, embeddingDims, topic: i % 3);
            ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = chunk, Embedding = new ReadOnlyMemory<float>(embedding) });
        }

        return ctx;
    }

    internal void SetupChatClient(string response)
    {
        ChatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
    }

    /// <summary>
    /// Configures the chat client to echo its prompt back as the "summary", instead of returning
    /// one fixed string for every cluster.
    /// </summary>
    /// <remarks>
    /// Every other caller of <see cref="SetupChatClient"/> returns the same literal text for every
    /// cluster, which is fine when a test only checks summary <em>mechanics</em> — but it erases
    /// each cluster's content, so a level-2 clustering sees identical text for every level-1
    /// summary and can find no real structure to cluster on. The prompt <c>SummarizeClusterAsync</c>
    /// sends contains the concatenated child chunk texts (<c>"Chunk N content about topic T"</c>,
    /// see <see cref="CreateContext"/>), so echoing it forward — rather than paraphrasing, which a
    /// canned string effectively does — keeps each "topic T" marker visible however many levels
    /// deep the tree goes. <see cref="BuildSummaryEmbedding"/> reads those markers back out.
    /// </remarks>
    internal void SetupChatClientToEchoPrompt()
    {
        ChatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var messages = callInfo.Arg<IEnumerable<ChatMessage>>()!.ToList();
                var text = string.Join(" ", messages.Select(m => m.Text));
                return new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]);
            });
    }

    /// <summary>Derives this document's embedding seed from its id, stably across processes.</summary>
    /// <remarks>
    /// Was <c>new Random(42)</c> on every call, which gave every document in a multi-document
    /// fixture byte-identical vectors: <c>RaptorCorpusBuildTests</c> ingested ten documents of two
    /// chunks each and clustered twenty points that were really two, repeated ten times apiece. A
    /// component of exact duplicates has zero variance, floors to <c>VarianceFloor</c>, and scores
    /// as a near-perfect fit, so those tests were measuring the variance floor rather than a corpus.
    /// Seeding per document keeps runs deterministic while letting different documents differ.
    ///
    /// FNV-1a rather than <see cref="string.GetHashCode()"/>: string hashing is randomised per
    /// process in .NET, so a fixture seeded from it would not reproduce across runs.
    /// </remarks>
    /// <param name="documentId">The document id to derive from.</param>
    /// <returns>A seed that depends only on <paramref name="documentId"/>.</returns>
    private static int SeedFor(string documentId)
    {
        unchecked
        {
            var hash = 2166136261;
            foreach (var c in documentId)
            {
                hash = (hash ^ c) * 16777619;
            }

            return (int)hash;
        }
    }

#pragma warning disable HLQ013 // Use foreach — need index-based assignment
    // Offset per topic rather than uniform over [0,1): the chunk text already claims to be
    // "about topic {i % 3}", and an embedding that ignores the topic contradicts the text it is
    // supposed to embed. It also carries no cluster structure whatsoever, so once SelectK stopped
    // isolating every point into its own component (#333) BIC read a whole context as a single
    // Gaussian — correctly — and no tree level could form. Three tight, well-separated blobs make
    // the vectors agree with the text, so clustering has something real to find.
    private static float[] GenerateEmbedding(Random rng, int dims, int topic)
    {
        var embedding = new float[dims];
        for (var j = 0; j < embedding.Length; j++)
            embedding[j] = topic + (float)(rng.NextDouble() * 0.1);
        return embedding;
    }
#pragma warning restore HLQ013

    internal void SetupEmbedder(int dims)
    {
        // rng is captured by the closure, not recreated per call: a real embedder returns
        // different vectors for different inputs, and RAPTOR's summary embedding calls are
        // always single-item batches, so re-seeding on every call made every summary chunk at
        // every tree level embed to the identical vector — collapsing every level above the
        // leaves into indistinguishable points and making a tree deeper than 1 level
        // unreachable in tests (see #332 test coverage gap).
        var rng = new Random(123);
        Embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>()!.ToList();
                return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                    new(texts.Select(text => new Embedding<float>(BuildSummaryEmbedding(text, dims, rng))).ToList()));
            });
    }

    /// <summary>Matches the "topic T" markers <see cref="CreateContext"/>'s leaf chunk text carries.</summary>
    private static readonly Regex TopicMarker =
        new(@"topic (?<topic>\d+)", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Gives a summary embedding the same treatment <see cref="CreateContext"/>'s leaf embeddings
    /// already get — real structure to cluster on — whenever the summary text carries a "topic T"
    /// marker (i.e. the chat client was set up with <see cref="SetupChatClientToEchoPrompt"/>).
    /// Every other caller's fixed summary text (<c>"a summary"</c>, <c>"Summary of cluster"</c>, …)
    /// contains no such marker, so this falls back to the original unstructured noise — this
    /// method changes nothing for any test that does not opt in.
    /// </summary>
    /// <remarks>
    /// Was pure noise regardless of input, which is why no test could reach depth 2 through
    /// BIC-selected <c>k</c> rather than an explicit <c>MaxClusters</c>: a level-2 clustering
    /// always saw unstructured noise for its input and correctly collapsed to one cluster. Topics
    /// 0 and 1 are placed close together and topic 2 far away — deliberately asymmetric — so that
    /// clustering the three level-1 summaries a well-separated <c>i % 3</c> leaf split produces
    /// finds a genuine two-tier hierarchy: {0, 1} merge into one supercluster, {2} stays alone,
    /// giving a real (non-degenerate) k = 2 at level 2. See
    /// <c>TreeReachesDepthTwo_WithBicSelectedK_NoMaxClustersSet</c>.
    /// </remarks>
    private static float[] BuildSummaryEmbedding(string text, int dims, Random rng)
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

        return Enumerable.Range(0, dims).Select(_ => (float)(center + rng.NextDouble() * 0.05)).ToArray();
    }

    /// <summary>Topics 0 and 1 sit close together; topic 2 sits far away — see <see cref="BuildSummaryEmbedding"/>.</summary>
    private static double PositionForTopic(int topic) => topic switch
    {
        0 => 0.0,
        1 => 0.3,
        _ => 10.0,
    };
}
