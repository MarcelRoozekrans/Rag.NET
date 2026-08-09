using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Api.DependencyInjection;
using Rag.NET.Mediator;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Api.Tests.DependencyInjection;

/// <summary>
/// The mapping-time guard against an exempt path prefix swallowing the API's own routes:
/// <c>AddRagNetWebhooks</c> cannot see the API's route prefix (it is chosen later, at
/// <c>MapRagNetApi</c>), so the collision is detected where both values are finally known —
/// when the API routes are mapped.
/// </summary>
public sealed class MapRagNetApiExemptionGuardTests
{
    private const string Secret = "webhook-secret";

    private sealed class NullQueue : IIngestionJobQueue
    {
        public ValueTask EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public IAsyncEnumerable<IngestionJob> DequeueAllAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Test double is enqueue-only.");
    }

    private static IHostBuilder BuildHost(string? webhookPrefix) =>
        new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IRagPipeline>());
                services.AddSingleton(Substitute.For<IRagMediator>());
                services.AddSingleton<IIngestionJobQueue>(new NullQueue());
                services.AddRagNetApi(o => o.ApiKeys = ["api-key"]);
                services.AddRagNetWebhooks(o =>
                {
                    o.Secret = Secret;
                    if (webhookPrefix is not null)
                        o.RoutePrefix = webhookPrefix;
                });
                services.AddRouting();
            })
            .Configure(app =>
            {
                app.UseRagNetApiAuthentication();
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapRagNetApi();
                    endpoints.MapRagNetWebhooks();
                });
            }));

    private static string Sign(string body) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)));

    [Theory]
    // A parent of every API route, and a prefix that covers exactly one route.
    [InlineData("/rag", "/rag/ingest")]
    [InlineData("/rag/ingest", "/rag/ingest")]
    public async Task WebhookPrefixCoveringAnApiRoute_ThrowsAtMapping_NamingPrefixAndRoute(
        string webhookPrefix, string exposedRoute)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildHost(webhookPrefix).StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains($"\"{webhookPrefix}\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"\"{exposedRoute}\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultWebhookPrefix_DoesNotCollide_WebhookStaysExemptAndApiStaysGuarded()
    {
        // "/rag/webhooks" lives UNDER the API prefix but is a parent of no API route — the
        // shipped default must keep working, and the exemption must keep covering it.
        using var host = await BuildHost(webhookPrefix: null)
            .StartAsync(TestContext.Current.CancellationToken);
        var client = host.GetTestClient();

        const string body = """{"documentId":"doc-1","content":"hello"}""";
        using var webhookRequest = new HttpRequestMessage(HttpMethod.Post, "/rag/webhooks/ingest");
        webhookRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
        webhookRequest.Headers.TryAddWithoutValidation("X-Signature-256", Sign(body));

        var webhookResponse = await client.SendAsync(webhookRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, webhookResponse.StatusCode);

        using var apiContent = new StringContent("{}", Encoding.UTF8, "application/json");
        var apiResponse = await client.PostAsync("/rag/retrieve", apiContent,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, apiResponse.StatusCode);
    }
}
