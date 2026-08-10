using System.Net;
using System.Text;
using Rag.NET.DataProviders.Web;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// Reproduces issue #123: a server declaring a charset .NET does not register by default.
/// <para>
/// The reporter's site serves <c>robots.txt</c> as
/// <c>content-type: text/plain;charset=ISO-8859-2</c>. .NET Core registers only a small set of
/// encodings, so <c>HttpContent.ReadAsStringAsync</c> — which <c>GetStringAsync</c> calls —
/// cannot honour that charset. Both fetch paths in the crawler catch
/// <see cref="HttpRequestException"/> only, so whatever it throws escapes and ends the crawl.
/// </para>
/// </summary>
public class WebCrawlerCharsetTests
{
    private const string Robots = "User-agent: *\nDisallow: /private/\n";
    private const string Page = "<html><body><p>Ahoj světe</p></body></html>";

    [Fact]
    public async Task RobotsWithUnregisteredCharset_DoesNotEndTheCrawl()
    {
        var provider = BuildProvider(robotsCharset: "ISO-8859-2", pageCharset: "utf-8");

        var entries = await CollectAsync(provider);

        Assert.NotEmpty(entries);
    }

    [Fact]
    public async Task PageWithUnregisteredCharset_IsSkippedRatherThanEndingTheCrawl()
    {
        // The same defect on the page path, which the issue did not mention: a single page in an
        // unsupported charset must not take the whole crawl down with it.
        var provider = BuildProvider(robotsCharset: "utf-8", pageCharset: "ISO-8859-2");

        var entries = await CollectAsync(provider);

        Assert.NotNull(entries);
    }

    [Fact]
    public async Task RobotsDirectivesAreStillHonouredWhenTheCharsetIsUnregistered()
    {
        // Falling back must not mean discarding the file: the Disallow still has to apply, or a
        // crawler silently starts fetching paths the site asked it not to.
        var provider = BuildProvider(robotsCharset: "ISO-8859-2", pageCharset: "utf-8");

        var entries = await CollectAsync(provider);

        Assert.DoesNotContain(entries, e => e.Contains("/private/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RssFeedWithUnregisteredCharset_IsStillRead()
    {
        // The likeliest real-world hit: legacy feeds routinely declare ISO-8859-1 or
        // windows-1252, and RssDataProvider had the same GetStringAsync call.
        const string Feed = """
            <?xml version="1.0"?>
            <rss version="2.0">
              <channel>
                <item><link>https://example.test/post-1</link></item>
              </channel>
            </rss>
            """;

        var handler = new CharsetHandler(
            new Dictionary<string, (string Body, string Charset)>(StringComparer.Ordinal)
            {
                ["https://example.test/feed.rss"] = (Feed, "ISO-8859-2"),
            });

        var sut = new RssDataProvider("https://example.test/feed.rss", new HttpClient(handler));

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            entries,
            e => string.Equals(e.Value.Id, "https://example.test/post-1", StringComparison.Ordinal));
    }

    private static WebCrawlerDataProvider BuildProvider(string robotsCharset, string pageCharset)
    {
        var handler = new CharsetHandler(
            new Dictionary<string, (string Body, string Charset)>(StringComparer.Ordinal)
            {
                ["https://example.test/"] = (Page, pageCharset),
                ["https://example.test/robots.txt"] = (Robots, robotsCharset),
            });

        return new WebCrawlerDataProvider(
            "https://example.test/",
            new HttpClient(handler),
            new WebCrawlerOptions { MaxPages = 5, MaxDepth = 1, RespectRobotsTxt = true });
    }

    private static async Task<List<string>> CollectAsync(WebCrawlerDataProvider provider)
    {
        var urls = new List<string>();
        await foreach (var entry in provider.GetFilesAsync(TestContext.Current.CancellationToken))
        {
            urls.Add(entry.Value.Id);
        }

        return urls;
    }

    private sealed class CharsetHandler(Dictionary<string, (string Body, string Charset)> responses)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (!responses.TryGetValue(url, out var response))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // Bytes written as ASCII deliberately: the point is the declared charset the client
            // cannot resolve, not the payload's actual encoding.
            var content = new ByteArrayContent(Encoding.ASCII.GetBytes(response.Body));
            content.Headers.TryAddWithoutValidation(
                "Content-Type", $"text/plain;charset={response.Charset}");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
