using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Exchange;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers.Email;
using Rag.NET.Storage;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class ExchangeMailDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    public ExchangeMailDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Exchange", "https://graph.microsoft.com");
    }

    private ExchangeMailDataProvider CreateProvider(ExchangeMailOptions? opts = null)
    {
        var http  = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        var graph = new GraphServiceClient(
            http,
            new AnonymousAuthenticationProvider(),
            _fixture.BaseUrl + "/v1.0");
        return new ExchangeMailDataProvider(
            graph, opts ?? new ExchangeMailOptions { Mailbox = "ingest@contoso.com" });
    }

    [Fact]
    public async Task GetFiles_YieldsEmlEntries()
    {
        var sut = CreateProvider();

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal("inbox/msg-1", results[0].Value.Id.Value);
        Assert.Equal("Quarterly Report.eml", results[0].Value.FileName);
        Assert.Equal("inbox/msg-2", results[1].Value.Id.Value);
        Assert.Equal("With Attachment.eml", results[1].Value.FileName);
        Assert.Equal("true", results[1].Value.Metadata!["has_attachments"]);
    }

    /// <summary>
    /// End-to-end proof of the rfc822 design decision: the connector's raw <c>$value</c>
    /// MIME streams are ingested through a registered <c>AddEmailParser()</c>, and the
    /// message with a text attachment exercises the Phase 1.5 attachment dispatcher
    /// (attachment content lands in the vector store via the existing text parser).
    /// </summary>
    [Fact]
    public async Task IngestFromProvider_EndToEnd_ParsesEmlAndDispatchesAttachment()
    {
        var ct    = TestContext.Current.CancellationToken;
        using var store = new InMemoryVectorStore();

        var services = new ServiceCollection();
        services.AddSingleton<IVectorStore>(store);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new FakeEmbeddingGenerator());
        services.AddRagNet(rag => rag.AddEmailParser());
        await using var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        var provider = CreateProvider();
        var result = await pipeline.IngestFromProviderAsync(
            provider,
            new ProviderId("exchange"),
            baseMetadata: new DocumentMetadata
            {
                DocumentId  = new DocumentId("exchange-base"),
                FileName    = "base.eml",
                ContentType = "message/rfc822",
            },
            cancellationToken: ct);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.IngestedCount);

        var stored = await store.SearchAsync(
            new float[] { 0.1f, 0.2f, 0.3f },
            new SearchOptions { TopK = 50 },
            ct);
        var texts = stored.Select(r => r.Chunk.Text).ToList();

        // Plain message body ingested from raw MIME.
        Assert.Contains(texts, t => t.Contains("Please find the quarterly numbers below", StringComparison.Ordinal));
        // Attachment content dispatched to the registered text parser.
        Assert.Contains(texts, t => t.Contains("Attached note content mentions the zebra project", StringComparison.Ordinal));
        // Connector metadata flows into chunk tags.
        Assert.Contains(stored, r =>
            r.Chunk.Metadata.TryGetValue("folder", out var folder)
            && folder == "inbox");

        // Watermark advanced to the max receivedDateTime for the caller to persist.
        Assert.Equal("2026-03-02T11:00:00.0000000+00:00", provider.GetDeltaToken());
    }

    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = values
                .Select(_ => new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f }))
                .ToList();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose() { }
    }
}
