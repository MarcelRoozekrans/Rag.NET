using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Api.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Api.Client.Tests;

public sealed class HttpRagPipelineIntegrationTests : IAsyncLifetime
{
    private readonly TestServer _testServer;
    private readonly IRagPipeline _mockPipeline;
    private readonly HttpRagPipeline _httpRagPipeline;

    public HttpRagPipelineIntegrationTests()
    {
        _mockPipeline = Substitute.For<IRagPipeline>();

        _mockPipeline
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>(
            [
                new SearchResult
                {
                    Score = 0.9,
                    Chunk = new TextChunk
                    {
                        Text = "chunk text",
                        DocumentId = new DocumentId("doc-1"),
                        ChunkIndex = 0
                    }
                }
            ]));

        _mockPipeline
            .IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionResult { DocumentId = new DocumentId("doc-1"), ChunksStored = 3 }));

        _mockPipeline
            .AskAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RagResponse
            {
                Answer = "42",
                Sources = []
            }));

        _mockPipeline
            .AskStreamingAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerableOf(
                new RagStreamingUpdate { TextDelta = "Hello" },
                new RagStreamingUpdate { TextDelta = " World" }));

#pragma warning disable ASPDEPR004 // WebHostBuilder is deprecated in favor of HostBuilder/WebApplicationBuilder — intentional for TestServer usage
#pragma warning disable ASPDEPR008 // TestServer(IWebHostBuilder) is deprecated — intentional for minimal test setup
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_mockPipeline);
                services.AddRagNetApi(o => o.ApiKeys = ["test-key"]);
                services.AddRouting();
            })
            .Configure(app =>
            {
                app.UseRagNetApiAuthentication();
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapRagNetApi());
            });
        _testServer = new TestServer(builder);
#pragma warning restore ASPDEPR008
#pragma warning restore ASPDEPR004

        var httpClient = _testServer.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", "test-key");
        _httpRagPipeline = new HttpRagPipeline(httpClient);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _testServer.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async IAsyncEnumerable<RagStreamingUpdate> AsyncEnumerableOf(params RagStreamingUpdate[] updates)
    {
        foreach (var update in updates)
        {
            yield return update;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task RetrieveAsync_ReturnsResults_FromServer()
    {
        var results = await _httpRagPipeline.RetrieveAsync("test query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("chunk text", results[0].Chunk.Text);
        Assert.Equal("doc-1", results[0].Chunk.DocumentId);
        Assert.Equal(0.9, results[0].Score);
    }

    [Fact]
    public async Task IngestAsync_ReturnsIngestionResult_FromServer()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("document content"));
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.txt" };

        var result = await _httpRagPipeline.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(3, result.ChunksStored);
    }

    [Fact]
    public async Task DeleteAsync_CompletesSuccessfully()
    {
        await _httpRagPipeline.DeleteAsync("doc-1", TestContext.Current.CancellationToken);

        await _mockPipeline.Received(1).DeleteAsync("doc-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_ReturnsAnswer_FromServer()
    {
        var response = await _httpRagPipeline.AskAsync("what is the answer?", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("42", response.Answer);
    }

    [Fact]
    public async Task AskStreamingAsync_YieldsTextDeltas_FromServer()
    {
        var deltas = new List<string?>();
        await foreach (var update in _httpRagPipeline.AskStreamingAsync("stream this", cancellationToken: TestContext.Current.CancellationToken))
        {
            deltas.Add(update.TextDelta);
        }

        Assert.Contains("Hello", deltas);
        Assert.Contains(" World", deltas);
    }
}
