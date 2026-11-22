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
using Rag.NET.Api.Tests.Shared;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;
using ZeroAlloc.Specification;

namespace Rag.NET.Api.Grpc.Client.Tests;

/// <summary>
/// The gRPC client carries the full <see cref="RagOptions"/> and <see cref="RetrievalOptions"/> to
/// the server, which hands its pipeline options equivalent to the caller's, defaults included (#944).
/// </summary>
public sealed class GrpcRagPipelineOptionsTests : IAsyncDisposable
{
    private readonly IRagPipeline _serverPipeline = Substitute.For<IRagPipeline>();
    private readonly List<RagOptions?> _askOptions = [];
    private readonly List<RetrievalOptions?> _retrieveOptions = [];
    private readonly TestServer _server;
    private readonly TestServer _strictServer;
    private readonly GrpcChannel _channel;
    private readonly GrpcChannel _strictChannel;

    public GrpcRagPipelineOptionsTests()
    {
        _serverPipeline.AskAsync(Arg.Any<string>(), Arg.Do<RagOptions?>(_askOptions.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RagResponse { Answer = "42", Sources = [] }));
        _serverPipeline.AskStreamingAsync(Arg.Any<string>(), Arg.Do<RagOptions?>(_askOptions.Add), Arg.Any<CancellationToken>())
            .Returns(_ => Stream("a", "b"));
        _serverPipeline.RetrieveAsync(Arg.Any<string>(), Arg.Do<RetrievalOptions?>(_retrieveOptions.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success(
                (IReadOnlyList<SearchResult>)Array.Empty<SearchResult>())));

        (_server, _channel) = CreateServer(allowPromptOverrides: true);
        (_strictServer, _strictChannel) = CreateServer(allowPromptOverrides: false);
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.ShutdownAsync().ConfigureAwait(false);
        await _strictChannel.ShutdownAsync().ConfigureAwait(false);
        _server.Dispose();
        _strictServer.Dispose();
    }

    public static TheoryData<string> AskCases() => new(OptionsAssert.AskCases().Select(c => c.Name));

    [Theory]
    [MemberData(nameof(AskCases))]
    public async Task AskAsync_CarriesTheOptions(string name)
    {
        var options = OptionsAssert.AskCase(name);

        await Client(_channel).AskAsync("q", options, TestContext.Current.CancellationToken);

        OptionsAssert.Equivalent(options, Assert.Single(_askOptions));
    }

    [Theory]
    [MemberData(nameof(AskCases))]
    public async Task AskStreamingAsync_CarriesTheOptions(string name)
    {
        var options = OptionsAssert.AskCase(name);

        var deltas = new List<string?>();
        await foreach (var update in Client(_channel).AskStreamingAsync("q", options, TestContext.Current.CancellationToken))
            deltas.Add(update.TextDelta);

        Assert.Equal(["a", "b"], deltas);
        OptionsAssert.Equivalent(options, Assert.Single(_askOptions));
    }

    [Fact]
    public async Task RetrieveAsync_CarriesEveryRetrievalOption()
    {
        var options = OptionsAssert.FullRetrieval();

        Assert.True((await Client(_channel).RetrieveAsync("q", options, TestContext.Current.CancellationToken)).IsSuccess);

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
        var client = Client(_channel);
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
    /// A raw request that sets no option at all, as a client in another language might send, gets
    /// the pipeline's defaults: every proto field is optional, and unset means the default.
    /// </summary>
    [Fact]
    public async Task EmptyProtoOptions_MapToThePipelinesDefaults()
    {
        var raw = new RagService.RagServiceClient(_channel);
        var ct = TestContext.Current.CancellationToken;

        await raw.AskAsync(new AskRequest { Query = "q", Options = new RagOptionsProto() }, cancellationToken: ct);
        await raw.RetrieveAsync(new RetrieveRequest { Query = "q", Options = new RetrievalOptionsProto() }, cancellationToken: ct);

        OptionsAssert.Equivalent(new RagOptions(), Assert.Single(_askOptions));
        OptionsAssert.Equivalent(new RetrievalOptions(), Assert.Single(_retrieveOptions));
    }

    [Fact]
    public async Task RetrievalWithAShortFormField_ThrowsBeforeSending_LikeRagPipeline()
    {
        var options = new RagOptions { TopK = 5, Retrieval = new RetrievalOptions() };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Client(_channel).AskAsync("q", options, TestContext.Current.CancellationToken));

        Assert.Equal("options", ex.ParamName);
        Assert.Contains(nameof(RagOptions.TopK), ex.Message, StringComparison.Ordinal);
        Assert.Empty(_askOptions);
    }

    [Fact]
    public async Task AFilterSpecification_IsRefused_RatherThanDropped()
    {
        var options = new RetrievalOptions { Filter = Substitute.For<ISpecification<SearchResult>>() };

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => Client(_channel).RetrieveAsync("q", options, TestContext.Current.CancellationToken));

        Assert.Contains("RetrievalOptions.Filter", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_retrieveOptions);
    }

    public static TheoryData<string> PromptFields() =>
    [
        "system_prompt", "temperature", "conversation_history",
        "map_reduce_options.reduce_prompt_template", "refine_options.initial_prompt_template",
    ];

    [Theory]
    [MemberData(nameof(PromptFields))]
    public async Task APromptShapingField_IsInvalidArgument_WhileTheServerDoesNotAllowIt(string field)
    {
        var options = field switch
        {
            "system_prompt" => new RagOptionsProto { SystemPrompt = "x" },
            "temperature" => new RagOptionsProto { Temperature = 0.5f },
            "conversation_history" => new RagOptionsProto { ConversationHistory = { new ChatMessageProto { Role = "user", Text = "hi" } } },
            "map_reduce_options.reduce_prompt_template" => new RagOptionsProto { MapReduceOptions = new MapReduceOptionsProto { ReducePromptTemplate = "x" } },
            _ => new RagOptionsProto { RefineOptions = new RefineOptionsProto { InitialPromptTemplate = "x" } },
        };
        var raw = new RagService.RagServiceClient(_strictChannel);
        var request = new AskRequest { Query = "q", Options = options };
        var ct = TestContext.Current.CancellationToken;

        var ask = await Assert.ThrowsAsync<RpcException>(async () => await raw.AskAsync(request, cancellationToken: ct));
        var stream = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using var call = raw.AskStream(request, cancellationToken: ct);
            while (await call.ResponseStream.MoveNext(ct)) { }
        });

        foreach (var ex in new[] { ask, stream })
        {
            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
            Assert.Contains($"options.{field}", ex.Status.Detail, StringComparison.Ordinal);
        }

        Assert.Empty(_askOptions);
    }

    [Fact]
    public async Task RetrievalFields_AreAcceptedWhileTheServerDoesNotAllowPromptOverrides()
    {
        await Client(_strictChannel).AskAsync(
            "q", new RagOptions { Retrieval = OptionsAssert.FullRetrieval(), SynthesisStrategy = SynthesisStrategy.Refine },
            TestContext.Current.CancellationToken);

        Assert.NotNull(Assert.Single(_askOptions)!.Retrieval);
    }

    [Fact]
    public async Task RetrievalWithAShortFormField_IsInvalidArgument_NamingTheFields()
    {
        var raw = new RagService.RagServiceClient(_channel);
        var request = new AskRequest
        {
            Query = "q",
            Options = new RagOptionsProto { TopK = 3, UseHybridSearch = true, Retrieval = new RetrievalOptionsProto { UseMmr = true } },
        };

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await raw.AskAsync(request, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("options.top_k", ex.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("options.use_hybrid_search", ex.Status.Detail, StringComparison.Ordinal);
        Assert.Empty(_askOptions);
    }

    private static GrpcRagPipeline Client(GrpcChannel channel) => new(new RagService.RagServiceClient(channel));

    private (TestServer Server, GrpcChannel Channel) CreateServer(bool allowPromptOverrides)
    {
#pragma warning disable ASPDEPR004 // WebHostBuilder deprecated — intentional for TestServer
#pragma warning disable ASPDEPR008 // TestServer(IWebHostBuilder) deprecated — intentional for minimal test setup
        var builder = new WebHostBuilder()
            .ConfigureKestrel(o => o.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http2))
            .ConfigureServices(services =>
            {
                services.AddSingleton(_serverPipeline);
                services.AddRagNetGrpcApi(o =>
                {
                    o.AllowAnonymous = true;
                    o.AllowClientPromptOverrides = allowPromptOverrides;
                });
                services.AddRouting();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapRagNetGrpcApi());
            });
        var server = new TestServer(builder);
#pragma warning restore ASPDEPR008
#pragma warning restore ASPDEPR004

        var httpClient = server.CreateClient();
        var channel = GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions { HttpClient = httpClient });
        return (server, channel);
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
