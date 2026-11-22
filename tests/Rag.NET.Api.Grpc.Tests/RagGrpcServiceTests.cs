using Grpc.Core;
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
using ZeroAlloc.Results;

namespace Rag.NET.Api.Grpc.Tests;

public sealed class RagGrpcServiceTests : IAsyncLifetime
{
    private readonly IRagPipeline _pipeline;
    private readonly TestServer _server;
    private readonly GrpcChannel _channel;
    private readonly GrpcChannel _channelNoKey;

    public RagGrpcServiceTests()
    {
        _pipeline = Substitute.For<IRagPipeline>();
        _pipeline.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success(
                (IReadOnlyList<SearchResult>)Array.Empty<SearchResult>())));
        _pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = new DocumentId("doc-1"), ChunksStored = 3 })));
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

        var httpClientWithKey = _server.CreateClient();
        httpClientWithKey.DefaultRequestHeaders.Add("x-api-key", "test-key");

        var httpClientNoKey = _server.CreateClient();

        _channel = GrpcChannel.ForAddress(httpClientWithKey.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = httpClientWithKey
        });

        _channelNoKey = GrpcChannel.ForAddress(httpClientNoKey.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = httpClientNoKey
        });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _channel.ShutdownAsync().ConfigureAwait(false);
        await _channelNoKey.ShutdownAsync().ConfigureAwait(false);
        _server.Dispose();
    }

    [Fact]
    public async Task Retrieve_ReturnsEmptyResults_WhenNoDocumentsIngested()
    {
        var client = new RagService.RagServiceClient(_channel);
        var response = await client.RetrieveAsync(
            new RetrieveRequest { Query = "test query" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Ingest_ReturnsDocumentId_WhenContentProvided()
    {
        var client = new RagService.RagServiceClient(_channel);
        var response = await client.IngestAsync(
            new IngestRequest { Content = "Hello world", FileName = "test.txt" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Equal("doc-1", response.DocumentId);
        Assert.Equal(3, response.ChunksStored);
    }

    [Fact]
    public async Task Retrieve_ReturnsUnauthenticated_WhenNoApiKey()
    {
        var client = new RagService.RagServiceClient(_channelNoKey);
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.RetrieveAsync(
                new RetrieveRequest { Query = "test" },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task Delete_Succeeds_WithValidKey()
    {
        var client = new RagService.RagServiceClient(_channel);
        var response = await client.DeleteAsync(
            new DeleteRequest { DocumentId = new DocumentId("doc-1") },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        await _pipeline.Received(1).DeleteAsync("doc-1", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A refused call, such as a spent cost budget, is ResourceExhausted rather than Internal (#937).
    /// </summary>
    [Fact]
    public async Task Retrieve_ARefusal_IsResourceExhausted()
    {
        _pipeline.RetrieveAsync("refused", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Failure(
                new RagError.CallRefused(new BudgetExceededException(CostWindow.Day, 1m, 1m)))));
        var client = new RagService.RagServiceClient(_channel);

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.RetrieveAsync(
                new RetrieveRequest { Query = "refused" },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }

    /// <summary>
    /// AskAsync reports a refusal by throwing: the refusal itself, or an InvalidOperationException
    /// around it when retrieval was refused. Ask and AskStream answer ResourceExhausted for either.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Ask_ARefusal_IsResourceExhausted(bool stream, bool wrapped)
    {
        var refusal = new BudgetExceededException(CostWindow.Day, 1m, 1m);
        Exception thrown = wrapped ? new InvalidOperationException("Retrieval failed", refusal) : refusal;
        _pipeline.AskAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<RagResponse>>(_ => throw thrown);
        _pipeline.AskStreamingAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Throwing(thrown));
        var client = new RagService.RagServiceClient(_channel);
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            if (!stream)
            {
                await client.AskAsync(new AskRequest { Query = "q" }, cancellationToken: ct);
                return;
            }

            using var call = client.AskStream(new AskRequest { Query = "q" }, cancellationToken: ct);
            while (await call.ResponseStream.MoveNext(ct)) { }
        });

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }

    private static async IAsyncEnumerable<RagStreamingUpdate> Throwing(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162 // Unreachable code: the yield makes this an iterator
        yield break;
#pragma warning restore CS0162
    }
}
