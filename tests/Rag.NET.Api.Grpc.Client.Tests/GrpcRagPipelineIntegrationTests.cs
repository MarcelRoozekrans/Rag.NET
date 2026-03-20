using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Api.Grpc.DependencyInjection;
using Rag.NET.Api.Grpc.Proto;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Api.Grpc.Client.Tests;

public sealed class GrpcRagPipelineIntegrationTests : IAsyncLifetime
{
    private readonly IRagPipeline _pipeline;
    private readonly TestServer _server;
    private readonly GrpcRagPipeline _grpcRagPipeline;

    public GrpcRagPipelineIntegrationTests()
    {
        _pipeline = Substitute.For<IRagPipeline>();

        _pipeline.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>(
            [
                new SearchResult
                {
                    Chunk = new TextChunk { Text = "Hello world", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 },
                    Score = 0.9
                }
            ]));

        _pipeline.IngestAsync(
                Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionResult { DocumentId = new DocumentId("doc-42"), ChunksStored = 5 }));

        _pipeline.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

#pragma warning disable ASPDEPR004 // WebHostBuilder deprecated — intentional for TestServer
#pragma warning disable ASPDEPR008 // TestServer(IWebHostBuilder) deprecated — intentional for minimal test setup
        var builder = new WebHostBuilder()
            .ConfigureKestrel(o => o.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http2))
            .ConfigureServices(services =>
            {
                services.AddSingleton(_pipeline);
                services.AddRagNetGrpcApi(o => o.ApiKeys = ["test-key"]);
                services.AddRouting();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapRagNetGrpcApi());
            });
        _server = new TestServer(builder);
#pragma warning restore ASPDEPR008
#pragma warning restore ASPDEPR004

        var httpClient = _server.CreateClient();
        httpClient.DefaultRequestHeaders.Add("x-api-key", "test-key");

        var channel = GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });

        var grpcClient = new RagService.RagServiceClient(channel);
        _grpcRagPipeline = new GrpcRagPipeline(grpcClient);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _server.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RetrieveAsync_ReturnsResults_WhenDocumentsIngested()
    {
        var results = await _grpcRagPipeline.RetrieveAsync(
            "test query",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(results);
        Assert.Single(results);
        Assert.Equal("Hello world", results[0].Chunk.Text);
        Assert.Equal("doc-1", results[0].Chunk.DocumentId);
        Assert.Equal(0.9, results[0].Score);
    }

    [Fact]
    public async Task IngestAsync_ReturnsIngestionResult_WhenContentProvided()
    {
        using var stream = new MemoryStream("Test content"u8.ToArray());
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-42"), FileName = "test.txt" };

        var result = await _grpcRagPipeline.IngestAsync(
            stream, metadata,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("doc-42", result.DocumentId);
        Assert.Equal(5, result.ChunksStored);
    }

    [Fact]
    public async Task DeleteAsync_CallsUnderlyingPipeline_WithDocumentId()
    {
        await _grpcRagPipeline.DeleteAsync("doc-1", TestContext.Current.CancellationToken);

        await _pipeline.Received(1).DeleteAsync("doc-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_MapsSearchResults_Correctly()
    {
        var results = await _grpcRagPipeline.RetrieveAsync(
            "another query",
            new RetrievalOptions { TopK = 3 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal(0, first.Chunk.ChunkIndex);
        Assert.Equal(0.9, first.Score);
    }
}
