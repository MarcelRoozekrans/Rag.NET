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
        Action<IServiceCollection>? servicesBeforeWebhooks = null,
        Action<Rag.NET.Api.Contracts.WebhookOptions>? configureOptions = null)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                servicesBeforeWebhooks?.Invoke(services);
                services.AddRagNetWebhooks(o =>
                {
                    o.Secret = Secret;
                    configureOptions?.Invoke(o);
                });
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
        string signatureHeader = "X-Signature-256", string? apiKey = null,
        string path = "/rag/webhooks/ingest")
    {
        var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
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

    [Theory]
    // jsonDocumentId is the JSON-escaped literal; expectedDocumentId is what it decodes to.
    // Traversal with forward slashes, both separators mixed, and an id that sanitizes to nothing.
    [InlineData("../../etc/passwd", "../../etc/passwd", ".._.._etc_passwd.txt")]
    [InlineData("a/b\\\\c", "a/b\\c", "a_b_c.txt")]
    [InlineData("...", "...", "document.txt")]
    public async Task HostileDocumentId_IsSanitizedIntoFileNameButKeptAsIdentity(
        string jsonDocumentId, string expectedDocumentId, string expectedFileName)
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue);
        var body = $$"""{"documentId":"{{jsonDocumentId}}","content":"hello"}""";

        var response = await PostAsync(server, body, Sign(body));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = Assert.Single(queue.Enqueued);
        Assert.Equal(expectedFileName, job.Metadata.FileName);

        // The traversal risk is the separators, not the dots: ".." with no separator either side
        // is an ordinary name fragment.
        Assert.DoesNotContain("/", job.Metadata.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", job.Metadata.FileName, StringComparison.Ordinal);

        // Identity is untouched — only the file name is a rendering concern.
        Assert.Equal(expectedDocumentId, job.Metadata.DocumentId.Value);
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
        Assert.Equal(["doc-1", "doc-2", "doc-3"], queue.Enqueued.Select(j => j.Metadata.DocumentId.Value), StringComparer.Ordinal);
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
    public async Task CustomRoutePrefix_MapsEndpointAndExemptsItFromApiKeyAuth()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue,
            servicesBeforeWebhooks: s => s.AddRagNetApi(o => o.ApiKeys = ["api-key"]),
            configureOptions: o => o.RoutePrefix = "/hooks");
        const string body = """{"documentId":"doc-1","content":"hello"}""";

        // No api key — only a valid signature — at the CUSTOM prefix.
        var response = await PostAsync(server, body, Sign(body), path: "/hooks/ingest");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(queue.Enqueued);
    }

    [Fact]
    public async Task CustomSignatureHeader_IsHonored()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue,
            configureOptions: o => o.SignatureHeader = "X-My-Signature");
        const string body = """{"documentId":"doc-1","content":"hello"}""";

        // Valid signature in the configured header → accepted.
        var ok = await PostAsync(server, body, Sign(body), signatureHeader: "X-My-Signature");
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);

        // Same signature but only in the DEFAULT header → configured header missing → 401.
        var wrongHeader = await PostAsync(server, body, Sign(body));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongHeader.StatusCode);
    }

    [Fact]
    public async Task SiblingPathSharingPrefixCharacters_StillRequiresApiKey()
    {
        var queue = new FakeQueue();
        using var server = CreateServer(queue,
            servicesBeforeWebhooks: s => s.AddRagNetApi(o => o.ApiKeys = ["api-key"]));

        // "/rag/webhooksfoo" shares leading characters with the exempt prefix but is a
        // different path segment — StartsWithSegments must NOT exempt it. Pins the
        // segment-aware matching against a future plain-StartsWith refactor.
        var client = server.CreateClient();
        var response = await client.PostAsync("/rag/webhooksfoo/ingest",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void AddRagNetWebhooks_EmptySecret_Throws()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() => services.AddRagNetWebhooks(o => o.Secret = " "));
        Assert.Contains("Secret", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("rag/webhooks")]
    public void AddRagNetWebhooks_InvalidRoutePrefix_Throws(string prefix)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() => services.AddRagNetWebhooks(o =>
        {
            o.Secret = Secret;
            o.RoutePrefix = prefix;
        }));
        Assert.Contains("RoutePrefix", ex.Message, StringComparison.Ordinal);
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
