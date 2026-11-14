using System.Net;
using System.Text;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// How <see cref="RobotsTxt.LoadAsync"/> reads each fetch result, per RFC 9309 section 2.3 (#998):
/// a 4xx allows everything, a 5xx, a 429 or no response at all disallows the whole host and
/// carries the error the provider reports, and only the first 500 KiB is parsed.
/// </summary>
public sealed class RobotsTxtLoadTests
{
    private const string RobotsUrl = "https://example.com/robots.txt";
    private static readonly Uri s_origin = new("https://example.com/");

    /// <summary>Answers robots.txt with a fixed status and body, or throws.</summary>
    private sealed class RobotsHandler(HttpStatusCode status, string body = "", Exception? failure = null)
        : HttpMessageHandler
    {
        public long BytesServed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (failure is not null) throw failure;
            var bytes = Encoding.UTF8.GetBytes(body);
            BytesServed = bytes.Length;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) });
        }
    }

    private static Task<RobotsTxt> LoadAsync(HttpMessageHandler handler, CancellationToken? ct = null) =>
        RobotsTxt.LoadAsync(new HttpClient(handler), s_origin, ct ?? TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    public async Task Status5xxOr429_IsUnreachable(int status)
    {
        var robots = await LoadAsync(new RobotsHandler((HttpStatusCode)status));

        var error = Assert.IsType<RagError.HttpFailed>(robots.Unreachable);
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains(RobotsUrl, error.Content, StringComparison.Ordinal);
        Assert.True(robots.IsDisallowed("https://example.com/"));
        Assert.True(robots.IsDisallowed("https://example.com/any/page"));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(410)]
    [InlineData(302)]
    public async Task Status4xxOr3xx_AllowsEverything(int status)
    {
        var robots = await LoadAsync(new RobotsHandler((HttpStatusCode)status));

        Assert.Null(robots.Unreachable);
        Assert.False(robots.IsDisallowed("https://example.com/a"));
    }

    public static TheoryData<string> NoResponseKinds => ["no-status", "io", "timeout", "proxy-tunnel"];

    [Theory]
    [MemberData(nameof(NoResponseKinds))]
    public async Task NoResponse_IsUnreachable(string kind)
    {
        Exception failure = kind switch
        {
            "no-status" => new HttpRequestException("Name or service not known"),
            "io" => new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
            // A proxy that refuses the CONNECT carries its status, but the site never answered.
            "proxy-tunnel" => new HttpRequestException(
                HttpRequestError.ProxyTunnelError, "The proxy tunnel request failed.", statusCode: HttpStatusCode.ProxyAuthenticationRequired),
            _ => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),
        };

        var robots = await LoadAsync(new RobotsHandler(HttpStatusCode.OK, failure: failure));

        var error = Assert.IsType<RagError.TransportFailed>(robots.Unreachable);
        var inner = Assert.IsType<HttpRequestException>(error.Inner);
        Assert.Contains(RobotsUrl, inner.Message, StringComparison.Ordinal);
        Assert.Same(failure, inner.InnerException);
        Assert.True(robots.IsDisallowed("https://example.com/"));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LoadAsync(new RobotsHandler(HttpStatusCode.OK, "User-agent: *\n"), cts.Token));
    }

    [Fact]
    public async Task Over500KiB_DropsThePartialLastLine()
    {
        const int cap = 512_000;
        var body = new StringBuilder("User-agent: *\n");
        var padding = "#" + new string('p', 98) + "\n";
        // Pad to just short of the cap, so the cap falls inside the rule's value.
        while (body.Length + padding.Length < cap - 13) body.Append(padding);
        body.Append('#', cap - 13 - body.Length - 1).Append('\n');
        // "Disallow: /ab" is 13 bytes and "Disallow: /abcdef" 17; byte 512,000 falls between them.
        body.Append("Disallow: /abcdef\n");
        body.Append(padding);
        var handler = new RobotsHandler(HttpStatusCode.OK, body.ToString());

        var robots = await LoadAsync(handler);

        Assert.True(handler.BytesServed > cap);
        Assert.Null(robots.Unreachable);
        Assert.False(robots.IsDisallowed("https://example.com/abz"));
        Assert.False(robots.IsDisallowed("https://example.com/abcdef"));
    }

    [Fact]
    public async Task Under500KiB_ParsesWholeFile()
    {
        var body = "User-agent: *\n" + string.Concat(Enumerable.Repeat("# padding line\n", 60)) + "Disallow: /last";

        var robots = await LoadAsync(new RobotsHandler(HttpStatusCode.OK, body));

        Assert.True(robots.IsDisallowed("https://example.com/last"));
    }
}
