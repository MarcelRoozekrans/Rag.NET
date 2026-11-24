using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Api.DependencyInjection;
using Rag.NET.Api.Tests.Shared;
using Rag.NET.Mediator;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;
using ZeroAlloc.Specification;

namespace Rag.NET.Api.Client.Tests;

/// <summary>
/// The HTTP client carries the full <see cref="RagOptions"/> and <see cref="RetrievalOptions"/> to
/// the server, which hands its pipeline options equivalent to the caller's, defaults included (#944).
/// </summary>
public sealed class HttpRagPipelineOptionsTests : IDisposable
{
    private readonly IRagPipeline _serverPipeline = Substitute.For<IRagPipeline>();
    private readonly IRagMediator _mediator = Substitute.For<IRagMediator>();
    private readonly List<RagOptions?> _askOptions = [];
    private readonly List<RetrievalOptions?> _retrieveOptions = [];
    private readonly TestServer _server;
    private readonly TestServer _strictServer;

    public HttpRagPipelineOptionsTests()
    {
        _serverPipeline.AskAsync(Arg.Any<string>(), Arg.Do<RagOptions?>(_askOptions.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RagResponse { Answer = "42", Sources = [] }));
        _serverPipeline.AskStreamingAsync(Arg.Any<string>(), Arg.Do<RagOptions?>(_askOptions.Add), Arg.Any<CancellationToken>())
            .Returns(_ => Stream("a", "b"));
#pragma warning disable EPS06 // ValueTask struct copy — intentional test double setup via NSubstitute
        _mediator.Send(Arg.Do<RetrieveQuery>(q => _retrieveOptions.Add(q.Options)), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<IReadOnlyList<SearchResult>, RagError>>(
                Result<IReadOnlyList<SearchResult>, RagError>.Success((IReadOnlyList<SearchResult>)Array.Empty<SearchResult>())));
#pragma warning restore EPS06

        _server = CreateServer(allowPromptOverrides: true);
        _strictServer = CreateServer(allowPromptOverrides: false);
    }

    public void Dispose()
    {
        _server.Dispose();
        _strictServer.Dispose();
    }

    public static TheoryData<string> AskCases() => new(OptionsAssert.AskCases().Select(c => c.Name));

    [Theory]
    [MemberData(nameof(AskCases))]
    public async Task AskAsync_CarriesTheOptions(string name)
    {
        var options = OptionsAssert.AskCase(name);

        await Client(_server).AskAsync("q", options, TestContext.Current.CancellationToken);

        OptionsAssert.Equivalent(options, Assert.Single(_askOptions));
    }

    [Theory]
    [MemberData(nameof(AskCases))]
    public async Task AskStreamingAsync_CarriesTheOptions(string name)
    {
        var options = OptionsAssert.AskCase(name);

        var deltas = new List<string?>();
        await foreach (var update in Client(_server).AskStreamingAsync("q", options, TestContext.Current.CancellationToken))
            deltas.Add(update.TextDelta);

        Assert.Equal(["a", "b"], deltas);
        OptionsAssert.Equivalent(options, Assert.Single(_askOptions));
    }

    [Fact]
    public async Task RetrieveAsync_CarriesEveryRetrievalOption()
    {
        var options = OptionsAssert.FullRetrieval();

        Assert.True((await Client(_server).RetrieveAsync("q", options, TestContext.Current.CancellationToken)).IsSuccess);

        OptionsAssert.Equivalent(options, Assert.Single(_retrieveOptions));
    }

    /// <summary>
    /// Defaults parity: an in-process caller that passes default options, or none, gets the
    /// pipeline's defaults. A remote caller now does too; before #944 the clients defaulted hybrid
    /// search to on.
    /// </summary>
    [Fact]
    public async Task NoOptions_AndDefaultOptions_ReachThePipelineAsTheyWouldInProcess()
    {
        var client = Client(_server);
        var ct = TestContext.Current.CancellationToken;

        await client.AskAsync("q", null, ct);
        await client.AskAsync("q", new RagOptions(), ct);
        Assert.True((await client.RetrieveAsync("q", null, ct)).IsSuccess);
        Assert.True((await client.RetrieveAsync("q", new RetrievalOptions(), ct)).IsSuccess);

        Assert.Null(_askOptions[0]);
        OptionsAssert.Equivalent(new RagOptions(), _askOptions[1]);
        Assert.False(_askOptions[1]!.UseHybridSearch);
        Assert.Null(_retrieveOptions[0]);
        OptionsAssert.Equivalent(new RetrievalOptions(), _retrieveOptions[1]);
        Assert.False(_retrieveOptions[1]!.UseHybridSearch);
    }

    /// <summary>
    /// A raw request with an empty options object, as a client in another language might send,
    /// gets the pipeline's defaults: every field is optional, and a missing one means the default.
    /// </summary>
    [Fact]
    public async Task EmptyOptionsObjects_MapToThePipelinesDefaults()
    {
        using var ask = await PostJsonAsync(_server, "/rag/ask", """{"query":"q","options":{"retrieval":{}}}""");
        using var retrieve = await PostJsonAsync(_server, "/rag/retrieve", """{"query":"q","options":{}}""");

        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retrieve.StatusCode);
        OptionsAssert.Equivalent(new RagOptions { Retrieval = new RetrievalOptions() }, Assert.Single(_askOptions));
        OptionsAssert.Equivalent(new RetrievalOptions(), Assert.Single(_retrieveOptions));
    }

    [Fact]
    public async Task RetrievalWithAShortFormField_ThrowsBeforeSending_LikeRagPipeline()
    {
        var options = new RagOptions { TopK = 5, Retrieval = new RetrievalOptions() };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Client(_server).AskAsync("q", options, TestContext.Current.CancellationToken));

        Assert.Equal("options", ex.ParamName);
        Assert.Contains(nameof(RagOptions.TopK), ex.Message, StringComparison.Ordinal);
        Assert.Empty(_askOptions);
    }

    [Fact]
    public async Task AFilterSpecification_IsRefused_RatherThanDropped()
    {
        var options = new RetrievalOptions { Filter = Substitute.For<ISpecification<SearchResult>>() };

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => Client(_server).RetrieveAsync("q", options, TestContext.Current.CancellationToken));

        Assert.Contains("RetrievalOptions.Filter", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_retrieveOptions);
    }

    [Theory]
    [InlineData("systemPrompt", """{"systemPrompt":"x"}""")]
    [InlineData("temperature", """{"temperature":0.5}""")]
    [InlineData("conversationHistory", """{"conversationHistory":[{"role":"user","text":"hi"}]}""")]
    [InlineData("mapReduceOptions.mapPromptTemplate", """{"mapReduceOptions":{"mapPromptTemplate":"x"}}""")]
    [InlineData("refineOptions.refinePromptTemplate", """{"refineOptions":{"refinePromptTemplate":"x"}}""")]
    public async Task APromptShapingField_IsRejectedWith400_WhileTheServerDoesNotAllowIt(string field, string optionsJson)
    {
        foreach (var path in new[] { "/rag/ask", "/rag/ask/stream" })
        {
            using var response = await PostJsonAsync(_strictServer, path, $$"""{"query":"q","options":{{optionsJson}}}""");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains($"options.{field}", body, StringComparison.Ordinal);
        }

        Assert.Empty(_askOptions);
    }

    [Fact]
    public async Task RetrievalFields_AreAcceptedWhileTheServerDoesNotAllowPromptOverrides()
    {
        await Client(_strictServer).AskAsync(
            "q", new RagOptions { Retrieval = OptionsAssert.FullRetrieval(), SynthesisStrategy = SynthesisStrategy.Refine },
            TestContext.Current.CancellationToken);

        Assert.NotNull(Assert.Single(_askOptions)!.Retrieval);
    }

    [Fact]
    public async Task RetrievalWithAShortFormField_IsRejectedWith400_NamingTheFields()
    {
        using var response = await PostJsonAsync(_server, "/rag/ask",
            """{"query":"q","options":{"topK":3,"useHybridSearch":true,"retrieval":{"useMmr":true}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("options.topK", body, StringComparison.Ordinal);
        Assert.Contains("options.useHybridSearch", body, StringComparison.Ordinal);
        Assert.Empty(_askOptions);
    }

    [Fact]
    public async Task TheGetStreamEndpoint_IsGone()
    {
        using var client = _server.CreateClient();

        using var response = await client.GetAsync("/rag/ask/stream?query=q", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(TestServer server, string path, string json)
    {
        using var client = server.CreateClient();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return await client.PostAsync(path, content, TestContext.Current.CancellationToken);
    }

    private static HttpRagPipeline Client(TestServer server) => new(server.CreateClient());

    private TestServer CreateServer(bool allowPromptOverrides)
    {
#pragma warning disable ASPDEPR004 // WebHostBuilder is deprecated in favor of HostBuilder/WebApplicationBuilder — intentional for TestServer usage
#pragma warning disable ASPDEPR008 // TestServer(IWebHostBuilder) is deprecated — intentional for minimal test setup
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_serverPipeline);
                services.AddSingleton(_mediator);
                services.AddRagNetApi(o =>
                {
                    o.AllowAnonymous = true;
                    o.AllowClientPromptOverrides = allowPromptOverrides;
                });
                services.AddRouting();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapRagNetApi());
            });
        return new TestServer(builder);
#pragma warning restore ASPDEPR008
#pragma warning restore ASPDEPR004
    }

    private static async IAsyncEnumerable<RagStreamingUpdate> Stream(params string[] deltas)
    {
        foreach (var delta in deltas)
        {
            await Task.Yield();
            yield return new RagStreamingUpdate { TextDelta = delta };
        }
    }
}
