using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Graph;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// Pins the response shapes a chat model actually returns for entity extraction.
/// <para>
/// The first case is not hypothetical. It is the shape llama-3.3-70b returned when the GraphRAG
/// pipeline was first run end to end: a sentence, then an unlabelled fence around good JSON.
/// <c>ExtractJson</c> stripped a fence only when the response <i>started</i> with one, so the
/// whole prose-plus-fence string reached the deserializer, threw, and was caught into a
/// <see langword="null"/> result. Every document extracted zero entities, ingestion reported
/// success, and the graph stayed empty — with nothing to see but one warning per chunk.
/// </para>
/// <para>
/// Unit tests over the parsing seam, deliberately, so the regression is caught on every pull
/// request rather than only when someone runs the gated container suite. The existing
/// <see cref="GraphEntityExtractionBehaviorTests"/> covered only pre-stripped JSON, which is why
/// a green suite coexisted with a pipeline that extracted nothing.
/// </para>
/// </summary>
public class ExtractionResponseShapeTests : IAsyncDisposable
{
    private const string Payload = """
        {"entities":[{"name":"Marie Curie","type":"Person","description":"Physicist"}],"relationships":[{"source":"Marie Curie","target":"Paris","description":"worked in","weight":1.0}]}
        """;

    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly SqliteGraphStore _graphStore = new(":memory:");

    public ValueTask DisposeAsync() => _graphStore.DisposeAsync();

    public static TheoryData<string, string> ResponseShapes() => new()
    {
        { "preamble then unlabelled fence", $"Here is the extracted information in JSON format:\n\n```\n{Payload}\n```" },
        { "preamble then labelled fence", $"Sure! Here you go:\n\n```json\n{Payload}\n```" },
        { "preamble then bare json", $"Here is the JSON:\n\n{Payload}" },
        { "labelled fence only", $"```json\n{Payload}\n```" },
        { "unlabelled fence only", $"```\n{Payload}\n```" },
        { "bare json", Payload },
        { "fence then trailing prose", $"```\n{Payload}\n```\n\nLet me know if you need anything else." },
    };

    [Theory]
    [MemberData(nameof(ResponseShapes))]
    public async Task EveryShapeAModelActuallyReturnsReachesTheGraph(string shape, string response)
    {
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

        _embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>()!.ToList();
                var rng = new Random(123);
                return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                    new(texts.Select(_ => new Embedding<float>(
                        Enumerable.Range(0, 8).Select(_ => (float)rng.NextDouble()).ToArray())).ToList()));
            });

        var sut = new GraphEntityExtractionBehavior(
            _chatClient, _embedder, _graphStore, new GraphRagOptions { GleaningPasses = 0 });

        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId("shape-doc"),
                FileName = "shape.txt",
            },
            GetNextBm25DocId = () => 0,
        };
        ctx.Chunks.Add(new TextChunk
        {
            Text = "Marie Curie worked in Paris.",
            DocumentId = new DocumentId("shape-doc"),
            ChunkIndex = 0,
        });

        await sut.HandleAsync(
            ctx,
            TestContext.Current.CancellationToken,
            (c, _) => ValueTask.FromResult(
                new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var snapshot = await _graphStore.GetFullGraphAsync(TestContext.Current.CancellationToken);

        Assert.True(
            snapshot.Entities.Count > 0,
            $"The '{shape}' response produced no entities. The JSON inside it is valid and names " +
            "Marie Curie, so ExtractJson failed to find it and the failure was swallowed into an " +
            "empty graph — the defect this test exists for.");

        Assert.Contains(
            snapshot.Entities,
            e => e.Name.Contains("Curie", StringComparison.OrdinalIgnoreCase));
    }
}
