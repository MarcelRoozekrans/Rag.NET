using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Api.DependencyInjection;
using Rag.NET.Api.Webhooks;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Api.Tests.Webhooks;

public sealed class WebhookEndpointTests
{
    private const string Secret = "test-secret";

    private sealed class FakeQueue : IIngestionJobQueue
    {
        public List<IngestionJob> Enqueued { get; } = [];

        public ValueTask EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(job);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<IngestionJob> DequeueAllAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Test double is enqueue-only.");
    }

    private sealed class FixedParser : IWebhookPayloadParser
    {
        public bool TryParse(JsonElement payload, [NotNullWhen(true)] out IReadOnlyList<IngestionJob>? jobs)
        {
            jobs =
            [
                new IngestionJob
                {
                    Content = Encoding.UTF8.GetBytes("custom content"),
                    Metadata = new DocumentMetadata
                    {
                        DocumentId = new DocumentId("custom-doc"),
                        FileName = "custom.bin",
                    },
                },
            ];
            return true;
        }
    }

#pragma warning disable ASPDEPR004 // WebHostBuilder is deprecated in favor of HostBuilder/WebApplicationBuilder — intentional for TestServer usage
#pragma warning disable ASPDEPR008 // TestServer(IWebHostBuilder) is deprecated — intentional for minimal test setup
    private static TestServer CreateServer(
        FakeQueue? queue,
        Action<IServiceCollection>? servicesBeforeWebhooks = null)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                servicesBeforeWebhooks?.Invoke(services);
                services.AddRagNetWebhooks(o => o.Secret = Secret);
                if (queue is not null)
                    services.AddSingleton<IIngestionJobQueue>(queue);
                services.AddRouting();
            })
            .Configure(app =>
            {
                app.UseRagNetApiAuthentication();
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapRagNetWebhooks());
            });
        return new TestServer(builder);
    }
#pragma warning restore ASPDEPR008
#pragma warning restore ASPDEPR004

    private static string Sign(string body) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)));

    private static async Task<HttpResponseMessage> PostAsync(
        TestServer server, string body, string? signature,
        string signatureHeader = "X-Signature-256", string? apiKey = null)
    {
        var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/rag/webhooks/ingest");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (signature is not null)
            request.Headers.TryAddWithoutValidation(signatureHeader, signature);
        if (apiKey is not null)
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<int> ReadEnqueuedCountAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("enqueued").GetInt32();
    }

    [Fact]
    public async Task ValidSignature_SingleDocument_Returns202AndEnqueuesJob()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue);
        const string body = """{"documentId":"doc-1","content":"hello world","metadata":{"source":"github"}}""";

        var response = await PostAsync(server, body, Sign(body));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await ReadEnqueuedCountAsync(response));
        var job = Assert.Single(queue.Enqueued);
        Assert.Equal("doc-1", job.Metadata.DocumentId.Value);
        Assert.Equal("doc-1.txt", job.Metadata.FileName);
        Assert.Equal("hello world", Encoding.UTF8.GetString(job.Content));
        Assert.Equal("github", job.Metadata.Tags["source"]);
    }

    [Fact]
    public async Task ValidSignature_ArrayPayload_EnqueuesAllJobs()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue);
        const string body = """
            [{"documentId":"doc-1","content":"one"},
             {"documentId":"doc-2","content":"two"},
             {"documentId":"doc-3","content":"three"}]
            """;

        var response = await PostAsync(server, body, Sign(body));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(3, await ReadEnqueuedCountAsync(response));
        Assert.Equal(["doc-1", "doc-2", "doc-3"], queue.Enqueued.Select(j => j.Metadata.DocumentId.Value));
    }

    [Fact]
    public async Task Sha256PrefixedSignature_Returns202()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue);
        const string body = """{"documentId":"doc-1","content":"hello"}""";

        var response = await PostAsync(server, body, $"sha256={Sign(body)}");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task BadSignature_Returns401AndQueueUntouched()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue);
        const string body = """{"documentId":"doc-1","content":"hello"}""";

        var response = await PostAsync(server, body, Sign("""{"tampered":true}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task MissingSignatureHeader_Returns401()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue);
        const string body = """{"documentId":"doc-1","content":"hello"}""";

        var response = await PostAsync(server, body, signature: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task InvalidJson_Returns400()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue);
        const string body = "this is not json {";

        var response = await PostAsync(server, body, Sign(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task PayloadMissingContent_Returns400()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue);
        const string body = """{"documentId":"doc-1"}""";

        var response = await PostAsync(server, body, Sign(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task NoQueueRegistered_Returns503WithActionableMessage()
    {
        using var server = CreateServer(queue: null);
        const string body = """{"documentId":"doc-1","content":"hello"}""";

        var response = await PostAsync(server, body, Sign(body));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("UseEventDrivenIngestion", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomParserRegisteredBeforeAddRagNetWebhooks_Wins()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue,
            servicesBeforeWebhooks: s => s.AddSingleton<IWebhookPayloadParser, FixedParser>());
        const string body = """{"anything":"goes"}""";

        var response = await PostAsync(server, body, Sign(body));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = Assert.Single(queue.Enqueued);
        Assert.Equal("custom-doc", job.Metadata.DocumentId.Value);
        Assert.Equal("custom.bin", job.Metadata.FileName);
    }

    [Fact]
    public async Task WebhookRoute_IsExemptFromApiKeyAuth_WhileOtherRoutesAreNot()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue,
            servicesBeforeWebhooks: s => s.AddRagNetApi(o => o.ApiKeys = ["api-key"]));
        const string body = """{"documentId":"doc-1","content":"hello"}""";

        // Webhook: NO api key, only a valid signature → passes the middleware and succeeds.
        var webhookResponse = await PostAsync(server, body, Sign(body));
        Assert.Equal(HttpStatusCode.Accepted, webhookResponse.StatusCode);

        // Any non-exempt route without an api key → 401 from the middleware.
        var client = server.CreateClient();
        var otherResponse = await client.PostAsync("/rag/ingest",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, otherResponse.StatusCode);
    }
}
