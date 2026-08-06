using System.Diagnostics;
using Azure;
using Azure.Core.Pipeline;
using AzureSearchClientOptions = Azure.Search.Documents.SearchClientOptions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

[Collection("Telemetry")]
public class AzureAISearchVectorStoreTelemetryTests : IAsyncLifetime
{
    private readonly IContainer _simulator = new ContainerBuilder("ghcr.io/ellerbach/azure-ai-search-simulator:latest")
        .WithPortBinding(8080, true)
        .WithPortBinding(8443, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on:"))
        .Build();

    private AzureAISearchVectorStore _sut = null!;
    private readonly string _indexName = $"ragnet-telemetry-{Guid.CreateVersion7():N}"[..24];

    public async ValueTask InitializeAsync()
    {
        await _simulator.StartAsync(TestContext.Current.CancellationToken);
        var httpsPort = _simulator.GetMappedPublicPort(8443);

        var httpHandler = new HttpClientHandler
        {
#pragma warning disable MA0039 // Do not write your own certificate validation method — intentional for local test simulator
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
#pragma warning restore MA0039
        };

        var options = new AzureSearchClientOptions
        {
            Transport = new HttpClientTransport(httpHandler),
        };

        _sut = new AzureAISearchVectorStore(
            new Uri($"https://localhost:{httpsPort}"),
            _indexName,
            new AzureKeyCredential("admin-key-12345"),
            vectorDimensions: 3,
            clientOptions: options);

        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _simulator.DisposeAsync();
    }

    [Fact]
    public async Task StoreSearchDelete_EmitVectorStoreSpansWithTags()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => string.Equals(s.Name, "Rag.NET", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("test-parent").Start();

        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "telemetry probe", DocumentId = new DocumentId("doc-telemetry"), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 1 },
            TestContext.Current.CancellationToken);
        await _sut.DeleteByDocumentIdAsync("doc-telemetry", TestContext.Current.CancellationToken);

        var ours = activities.Where(a => a.TraceId == parent.TraceId).ToList();

        var upsertSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.upsert", StringComparison.Ordinal));
        Assert.NotNull(upsertSpan);
        Assert.Equal(nameof(AzureAISearchVectorStore), upsertSpan.GetTagItem("vector.store"));
        Assert.Equal(_indexName, upsertSpan.GetTagItem("vectorstore.collection"));
        Assert.Equal(1, upsertSpan.GetTagItem("vectorstore.batch.size"));

        var searchSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.search", StringComparison.Ordinal));
        Assert.NotNull(searchSpan);
        Assert.Equal(nameof(AzureAISearchVectorStore), searchSpan.GetTagItem("vector.store"));

        var deleteSpan = ours.SingleOrDefault(a => string.Equals(a.OperationName, "ragnet.vectorstore.delete", StringComparison.Ordinal));
        Assert.NotNull(deleteSpan);
        Assert.Equal(nameof(AzureAISearchVectorStore), deleteSpan.GetTagItem("vector.store"));
    }
}
