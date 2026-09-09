using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents.Indexes;
using AzureSearchClientOptions = Azure.Search.Documents.SearchClientOptions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

/// <summary>
/// The ranker needs a semantic configuration on the index, and the store owns its name rather than
/// exposing it — one configuration, built from the fields the store already defines (#328).
/// </summary>
[Collection("AzureAISearch")]
public class AzureAISearchSemanticConfigurationTests : IAsyncLifetime
{
    private readonly IContainer _simulator = new ContainerBuilder("ghcr.io/ellerbach/azure-ai-search-simulator:latest")
        .WithPortBinding(8080, true)
        .WithPortBinding(8443, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on:"))
        .Build();

    private Uri _endpoint = null!;
    private AzureKeyCredential _credential = null!;
    private AzureSearchClientOptions _clientOptions = null!;

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

        _endpoint = new Uri($"https://localhost:{httpsPort}");
        _credential = new AzureKeyCredential("admin-key-12345");
        _clientOptions = new AzureSearchClientOptions { Transport = new HttpClientTransport(httpHandler) };
    }

    public async ValueTask DisposeAsync()
    {
        await _simulator.DisposeAsync();
    }

    /// <summary>
    /// The ranker needs a semantic configuration on the index, and the store owns its name rather
    /// than exposing it — one configuration, built from the fields the store already defines.
    /// </summary>
    [Fact]
    public async Task EnablingTheRanker_AddsASemanticConfigurationToTheIndex()
    {
        var indexName = $"ragnet-sem-{Guid.CreateVersion7():N}"[..24];
        using var sut = new AzureAISearchVectorStore(
            _endpoint,
            indexName,
            _credential,
            vectorDimensions: 3,
            _clientOptions,
            new AzureAISearchOptions { EnableSemanticRanking = true });

        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        var indexClient = new SearchIndexClient(_endpoint, _credential, _clientOptions);
        var index = await indexClient.GetIndexAsync(indexName, TestContext.Current.CancellationToken);

        Assert.NotNull(index.Value.SemanticSearch);
        var configuration = Assert.Single(index.Value.SemanticSearch!.Configurations);
        var contentField = Assert.Single(configuration.PrioritizedFields.ContentFields);
        Assert.Equal("text", contentField.FieldName);
    }

    /// <summary>
    /// And an index built without the ranker carries none: a configuration nothing uses is
    /// clutter, and adding it unconditionally would rewrite every existing index for no benefit.
    /// </summary>
    [Fact]
    public async Task WithoutTheRanker_TheIndexHasNoSemanticConfiguration()
    {
        var indexName = $"ragnet-sem-{Guid.CreateVersion7():N}"[..24];
        using var sut = new AzureAISearchVectorStore(
            _endpoint,
            indexName,
            _credential,
            vectorDimensions: 3,
            _clientOptions,
            options: null);

        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        var indexClient = new SearchIndexClient(_endpoint, _credential, _clientOptions);
        var index = await indexClient.GetIndexAsync(indexName, TestContext.Current.CancellationToken);

        // Present-but-empty is not the same as absent: a future edit that unconditionally sets
        // index.SemanticSearch = new SemanticSearch() would rewrite every existing index on the
        // next initialisation, and this must fail if that happens.
        Assert.Null(index.Value.SemanticSearch);
    }
}
