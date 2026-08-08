using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Linear;
using Rag.NET.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class LinearDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    public LinearDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Linear", "https://api.linear.app");
    }

    private LinearDataProvider CreateProvider(Action<LinearOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLinearDataProvider(
            apiKey: "lin_api_test",
            configure: configure,
            baseUrl: _fixture.BaseUrl);
        var sp = services.BuildServiceProvider();
        return Assert.IsType<LinearDataProvider>(sp.GetRequiredService<IFileContentProvider>());
    }

    [Fact]
    public async Task GetFiles_YieldsIssueMarkdownEntries()
    {
        var sut = CreateProvider();

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal("ENG-1", results[0].Value.Id.Value);
        Assert.Equal("ENG-1 Fix login bug.md", results[0].Value.FileName);
        Assert.Equal("ENG", results[0].Value.Metadata!["team"]);
        Assert.Equal("ENG-2 Add audit log.md", results[1].Value.FileName);

        // The traversal completed, so the watermark advanced to the max updatedAt.
        Assert.Equal("2026-07-02T08:00:00.0000000+00:00", sut.GetDeltaToken());
    }

    [Fact]
    public async Task GetFiles_PostsIssuesQueryWithVariablesAndBareApiKey()
    {
        _fixture.Server.ResetLogEntries();

        var sut = CreateProvider(opts =>
        {
            opts.TeamKeys = ["ENG"];
            opts.States   = ["started"];
        });

        await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(_fixture.Server.LogEntries);
        var request = entry.RequestMessage;
        Assert.NotNull(request);
        Assert.Equal("/graphql", request.AbsolutePath);
        Assert.Equal("POST", request.Method);

        // The GraphQL envelope carries the issues query document plus typed variables.
        var body = request.Body;
        Assert.NotNull(body);
        Assert.Contains("query Issues($first: Int!, $after: String, $filter: IssueFilter)", body, StringComparison.Ordinal);
        Assert.Contains("orderBy: updatedAt", body, StringComparison.Ordinal);
        Assert.Contains("comments(first: 100)", body, StringComparison.Ordinal);
        Assert.Contains("\"first\":50", body, StringComparison.Ordinal);
        Assert.Contains("\"team\":{\"key\":{\"in\":[\"ENG\"]}}", body, StringComparison.Ordinal);
        Assert.Contains("\"state\":{\"type\":{\"in\":[\"started\"]}}", body, StringComparison.Ordinal);
        // No delta token configured: the updatedAt member is omitted, not null.
        Assert.DoesNotContain("updatedAt\":null", body, StringComparison.Ordinal);

        // Linear auth is the BARE api key — no Bearer prefix (see LinearDataProviderExtensions).
        var headers = request.Headers;
        Assert.NotNull(headers);
        Assert.Equal("lin_api_test", Assert.Single(headers["Authorization"]));
    }

    [Fact]
    public async Task GetFiles_CursorPagination_Followed()
    {
        const string scenario   = "linear-cursor";
        const string page2State = "page-2";

        const string page1Body =
            """{"data":{"issues":{"nodes":[{"identifier":"ENG-1","title":"First","description":null,"updatedAt":"2026-07-01T12:00:00.000Z","url":"https://linear.app/acme/issue/ENG-1","state":null,"project":null,"assignee":null,"team":null,"comments":{"nodes":[]}}],"pageInfo":{"hasNextPage":true,"endCursor":"cursor123"}}}}""";
        const string page2Body =
            """{"data":{"issues":{"nodes":[{"identifier":"ENG-2","title":"Second","description":null,"updatedAt":"2026-07-02T12:00:00.000Z","url":"https://linear.app/acme/issue/ENG-2","state":null,"project":null,"assignee":null,"team":null,"comments":{"nodes":[]}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""";

        _fixture.Server.ResetMappings();

        // Page 1: returns a cursor; transitions scenario to page-2.
        _fixture.Server
            .Given(Request.Create().WithPath("/graphql").UsingPost())
            .InScenario(scenario)
            .WillSetStateTo(page2State)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json; charset=utf-8")
                .WithBody(page1Body));

        // Page 2: matches only after the state transition; final page.
        _fixture.Server
            .Given(Request.Create().WithPath("/graphql").UsingPost())
            .InScenario(scenario)
            .WhenStateIs(page2State)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json; charset=utf-8")
                .WithBody(page2Body));

        _fixture.Server.ResetLogEntries();

        var sut = CreateProvider();

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("ENG-1", results[0].Value.Id.Value);
        Assert.Equal("ENG-2", results[1].Value.Id.Value);

        var requests = _fixture.Server.LogEntries.ToList();
        Assert.Equal(2, requests.Count);
        Assert.Contains(requests, e =>
        {
            var eRequest = e.RequestMessage;
            Assert.NotNull(eRequest);
            var body = eRequest.Body;
            return body != null && !body.Contains("\"after\":", StringComparison.Ordinal);
        });
        Assert.Contains(requests, e =>
        {
            var eRequest = e.RequestMessage;
            Assert.NotNull(eRequest);
            var body = eRequest.Body;
            return body != null && body.Contains("\"after\":\"cursor123\"", StringComparison.Ordinal);
        });
    }
}
