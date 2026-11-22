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
using Rag.NET.Mediator;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Api.Tests.Integration;

/// <summary>
/// How a failed <see cref="RagError"/> reaches an HTTP caller. A refused call, such as a spent cost
/// budget, is 429 rather than a 500 that reads like a broken store (#937). A failed model call is
/// an upstream failure, 502, like the other upstream cases.
/// </summary>
public sealed class RagErrorStatusTests
{
    public static TheoryData<string, HttpStatusCode> Cases => new()
    {
        { "refused", HttpStatusCode.TooManyRequests },
        { "model", HttpStatusCode.BadGateway },
        { "storage", HttpStatusCode.InternalServerError },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Ingest_MapsTheErrorToItsStatus(string kind, HttpStatusCode expected)
    {
        RagError error = kind switch
        {
            "refused" => new RagError.CallRefused(new BudgetExceededException(CostWindow.Day, 1m, 1m)),
            "model" => new RagError.ModelCallFailed(new ModelCallException("rate limited", new InvalidOperationException("429"))),
            _ => new RagError.StorageFailed(new InvalidOperationException("store down")),
        };
        var mediator = Substitute.For<IRagMediator>();
#pragma warning disable EPS06 // ValueTask struct copy — intentional test double setup via NSubstitute
        mediator.Send(Arg.Any<IngestCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<IngestionResult, RagError>>(
                Result<IngestionResult, RagError>.Failure(error)));
#pragma warning restore EPS06

        using var server = CreateServer(mediator);
        using var client = server.CreateClient();

        using var response = await client.PostAsJsonAsync("/rag/ingest",
            new IngestRequest { Content = "hello", FileName = "test.txt" },
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    private static TestServer CreateServer(IRagMediator mediator)
    {
#pragma warning disable ASPDEPR004 // WebHostBuilder is deprecated in favor of HostBuilder/WebApplicationBuilder — intentional for TestServer usage
#pragma warning disable ASPDEPR008 // TestServer(IWebHostBuilder) is deprecated — intentional for minimal test setup
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IRagPipeline>());
                services.AddSingleton(mediator);
                services.AddRagNetApi(o => o.AllowAnonymous = true);
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
}
