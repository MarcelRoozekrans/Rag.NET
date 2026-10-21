using Azure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Storage;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.Parsers.AzureDocumentIntelligence.Tests;

/// <summary>
/// The whole path a scan takes in a real host: a <c>.tif</c> with no declared content type is
/// typed from its extension by the ingestion pipeline, routed to this parser by its claim, sent
/// to the (WireMock-backed) service and stored. The unit tests cover each step in isolation;
/// this one fails if any link between them breaks, such as the extension map losing
/// <c>.tif</c>, which would resolve the scan to <c>text/plain</c> and ingest its bytes as text.
/// </summary>
/// <remarks>Modelled on <c>ReIngestReplaceTests</c> in <c>Rag.NET.Tests</c>.</remarks>
[Collection("WireMock")]
public sealed class AzureDocumentIntelligencePipelineTests
{
    private readonly WireMockServerFixture _fixture;

    public AzureDocumentIntelligencePipelineTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("AzureDocumentIntelligence");
        _fixture.Server.ResetLogEntries();
    }

    [Fact]
    public async Task IngestAsync_TifWithoutContentType_IsParsedByThisParserAndStored()
    {
        var ct = TestContext.Current.CancellationToken;
        using var vectorStore = new InMemoryVectorStore();
        var services = new ServiceCollection();
        services.AddSingleton<IVectorStore>(vectorStore);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new ConstantEmbedder());
        services.AddRagNet(rag => rag.AddAzureDocumentIntelligenceParser(
            new Uri(_fixture.BaseUrl),
            new AzureKeyCredential("cassette-key"),
            o =>
            {
                o.Analyzer.ModelId = "tiff-three-frames";
                o.Analyzer.PollingInterval = TimeSpan.Zero;
            }));
        await using var provider = services.BuildServiceProvider();
        var ingestor = provider.GetRequiredService<IIngestor>();

        using var stream = new MemoryStream([0x49, 0x49, 0x2A, 0x00]);
        var result = await ingestor.IngestAsync(
            stream,
            new DocumentMetadata
            {
                DocumentId = new DocumentId("scan-1"),
                FileName = "scan.tif",
                ContentType = null,
            },
            options: null,
            cancellationToken: ct);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.ToString());
        Assert.True(result.Value.ChunksStored > 0);
        Assert.Contains(
            _fixture.Server.LogEntries,
            e => e.RequestMessage?.Path?.EndsWith("tiff-three-frames:analyze", StringComparison.Ordinal) == true);

        // The stored text is the service's, not the TIFF's bytes read as text.
        var hits = provider.GetRequiredService<IBm25Index>().Search("Frame", topK: 10);
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("scan-1", h.chunk.DocumentId.Value));
        Assert.Contains(hits, h => h.chunk.Text.Contains("Frame two text.", StringComparison.Ordinal));
    }

    /// <summary>Embeds every chunk to the same vector; the test asserts on parsing, not ranking.</summary>
    private sealed class ConstantEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
            {
                embeddings.Add(new Embedding<float>(new float[] { 1f, 0f }));
            }

            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        /// <summary>Nothing to release: the fake holds no disposable state.</summary>
        public void Dispose()
        {
        }
    }
}
