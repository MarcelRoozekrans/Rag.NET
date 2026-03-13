using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Api.Contracts;
using Rag.NET.Api.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Api.Tests.Integration;

public sealed class RagApiIntegrationTests : IAsyncLifetime
{
    private readonly HttpClient _client;
    private readonly HttpClient _clientWithKey;
    private readonly TestServer _testServer;

    public RagApiIntegrationTests()
    {
        var pipeline = Substitute.For<IRagPipeline>();
        pipeline.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionResult { DocumentId = "doc-1", ChunksStored = 1 }));

#pragma warning disable ASPDEPR004 // WebHostBuilder is deprecated in favor of HostBuilder/WebApplicationBuilder — intentional for TestServer usage
#pragma warning disable ASPDEPR008 // TestServer(IWebHostBuilder) is deprecated — intentional for minimal test setup
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(pipeline);
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
        _client = _testServer.CreateClient();
        _clientWithKey = _testServer.CreateClient();
        _clientWithKey.DefaultRequestHeaders.Add("X-Api-Key", "test-key");
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _clientWithKey.Dispose();
        _testServer.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Retrieve_Returns401_WhenNoApiKey()
    {
        var response = await _client.PostAsJsonAsync("/rag/retrieve",
            new RetrieveRequest { Query = "test" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Retrieve_Returns200_WithValidKey()
    {
        var response = await _clientWithKey.PostAsJsonAsync("/rag/retrieve",
            new RetrieveRequest { Query = "test" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RetrieveResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Empty(body.Results);
    }

    [Fact]
    public async Task Ingest_Returns200_WithValidKey()
    {
        var response = await _clientWithKey.PostAsJsonAsync("/rag/ingest",
            new IngestRequest { Content = "hello", FileName = "test.txt" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("doc-1", body.DocumentId);
    }

    [Fact]
    public async Task Delete_Returns204_WithValidKey()
    {
        var response = await _clientWithKey.DeleteAsync("/rag/documents/doc-1",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
