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
using Rag.NET.Mediator;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Api.Client.Tests;

public sealed class HttpRagPipelineIntegrationTests : IAsyncLifetime
{
    private readonly TestServer _testServer;
    private readonly IRagPipeline _mockPipeline;
    private readonly IRagMediator _mockMediator;
    private readonly HttpRagPipeline _httpRagPipeline;

    public HttpRagPipelineIntegrationTests()
    {
        _mockPipeline = CreateMockPipeline();
        _mockMediator = CreateMockMediator();

#pragma warning disable ASPDEPR004 // WebHostBuilder is deprecated in favor of HostBuilder/WebApplicationBuilder — intentional for TestServer usage
#pragma warning disable ASPDEPR008 // TestServer(IWebHostBuilder) is deprecated — intentional for minimal test setup
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_mockPipeline);
                services.AddSingleton(_mockMediator);
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

    private static IRagPipeline CreateMockPipeline()
    {
        var mock = Substitute.For<IRagPipeline>();

        mock.AskAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RagResponse { Answer = "42", Sources = [] }));

        mock.AskStreamingAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerableOf(
                new RagStreamingUpdate { TextDelta = "Hello" },
                new RagStreamingUpdate { TextDelta = " World" }));

        return mock;
    }

    private static IRagMediator CreateMockMediator()
    {
        var mock = Substitute.For<IRagMediator>();

#pragma warning disable EPS06 // ValueTask struct copy — intentional test double setup via NSubstitute
        mock.Send(Arg.Any<RetrieveQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<IReadOnlyList<SearchResult>, RagError>>(
                Result<IReadOnlyList<SearchResult>, RagError>.Success(
                    (IReadOnlyList<SearchResult>)new List<SearchResult>
                    {
                        new SearchResult
                        {
                            Score = 0.9,
                            Chunk = new TextChunk { Text = "chunk text", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 }
                        }
                    })));

        mock.Send(Arg.Any<IngestCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<IngestionResult, RagError>>(
                Result<IngestionResult, RagError>.Success(
                    new IngestionResult { DocumentId = new DocumentId("doc-1"), ChunksStored = 3 })));

        mock.Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<Unit, RagError>>(
                Result<Unit, RagError>.Success(Unit.Value)));
#pragma warning restore EPS06

        return mock;
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
        var result = await _httpRagPipeline.RetrieveAsync("test query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal("chunk text", result.Value[0].Chunk.Text);
        Assert.Equal("doc-1", result.Value[0].Chunk.DocumentId);
        Assert.Equal(0.9, result.Value[0].Score);
    }

    [Fact]
    public async Task IngestAsync_ReturnsIngestionResult_FromServer()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("document content"));
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.txt" };

        var result = await _httpRagPipeline.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("doc-1", result.Value.DocumentId);
        Assert.Equal(3, result.Value.ChunksStored);
    }

    [Fact]
    public async Task DeleteAsync_CompletesSuccessfully()
    {
        await _httpRagPipeline.DeleteAsync("doc-1", TestContext.Current.CancellationToken);

        _ = await _mockMediator.Received(1).Send(
            Arg.Is<DeleteCommand>(c => c!.DocumentId.ToString() == "doc-1"),
            Arg.Any<CancellationToken>());
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

        Assert.Contains("Hello", deltas, StringComparer.Ordinal);
        Assert.Contains(" World", deltas, StringComparer.Ordinal);
    }
}
