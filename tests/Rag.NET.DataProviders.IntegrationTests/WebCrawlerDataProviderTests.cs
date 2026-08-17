using Rag.NET.DataProviders.Web;
using Rag.NET.Models;
using Rag.NET.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.IntegrationTests;

/// <summary>
/// Crawls a real multi-page site over real HTTP — the surface of
/// <c>Rag.NET.DataProviders.Web</c> that had no integration coverage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Milestone 6, Phase 6.2.</b> The package's allowlist entry read "6.1 — live HTTP crawl,
/// sitemap and RSS (a local server is a 6.2 option; decide in 6.1)". Two of those three have had
/// WireMock coverage since #269; the crawler was the one left, and it is the only one of the three
/// with non-trivial control flow — breadth-first traversal, a visited set, four options that gate
/// it, and robots.txt parsing. This decides the question the entry left open: a local server is
/// enough, so nothing here needs a live site or a credential.
/// </para>
/// <para>
/// <b>Why a server rather than a substituted <see cref="HttpMessageHandler"/>.</b> A handler
/// substitute would return canned HTML per URL, which tests the traversal against a script the test
/// already wrote. WireMock is a real socket: the crawler resolves relative hrefs against a real
/// authority, AngleSharp parses real bytes off the wire, and the charset fallback in
/// <c>GetStringWithCharsetFallbackAsync</c> runs against real response headers. The site below has a
/// cycle, an unreachable-by-robots path and a second host, none of which a per-URL script exercises.
/// </para>
/// <para>
/// <b>The site, drawn out because every assertion depends on its shape:</b>
/// </para>
/// <code>
///   /                 depth 0   links -> /a, /b, and 127.0.0.1 (a different host, same server)
///   /a                depth 1   links -> /a1
///   /b                depth 1   links -> /private/secret
///   /a1               depth 2   links -> /            (a cycle, back to the seed)
///   /private/secret   depth 2   reachable, and disallowed by /robots.txt
/// </code>
/// <para>
/// The cross-host link is <c>127.0.0.1</c> against a server bound to <c>localhost</c>: the same
/// socket under a different <c>Host</c>. That makes the same-domain rule testable in <b>both</b>
/// directions — an <c>example.invalid</c> link would only ever prove that an unreachable host is
/// unreachable, which is not what <see cref="WebCrawlerOptions.SameDomain"/> decides.
/// </para>
/// </remarks>
[Collection("WireMock")]
public sealed class WebCrawlerDataProviderTests
{
    private readonly WireMockServerFixture _fixture;
    private readonly string _crossHostSeed;

    public WebCrawlerDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        var baseUrl = _fixture.BaseUrl;
        _crossHostSeed = baseUrl
            .Replace("localhost", "127.0.0.1", StringComparison.Ordinal);

        StubPage("/", $"""
            <html><body>
              <a href="/a">A</a>
              <a href="/b">B</a>
              <a href="{_crossHostSeed}/a">same server, different host</a>
            </body></html>
            """);
        StubPage("/a", "<html><body><p>Page A body text.</p><a href=\"/a1\">A1</a></body></html>");
        StubPage("/b", "<html><body><p>Page B body text.</p><a href=\"/private/secret\">secret</a></body></html>");

        // The cycle. A crawler without a visited set loops here forever rather than failing, which
        // is why the page-count assertions below are exact rather than lower bounds.
        StubPage("/a1", "<html><body><p>Page A1.</p><a href=\"/\">home</a></body></html>");
        StubPage("/private/secret", "<html><body><p>Should not be crawled by default.</p></body></html>");

        _fixture.Server
            .Given(Request.Create().WithPath("/robots.txt").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain; charset=utf-8")
                .WithBody("User-agent: *\nDisallow: /private\n"));
    }

    private void StubPage(string path, string html) =>
        _fixture.Server
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/html; charset=utf-8")
                .WithBody(html));

    private HttpClient CreateHttpClient() => new() { BaseAddress = new Uri(_fixture.BaseUrl) };

    private async Task<List<FileEntry>> CrawlAsync(WebCrawlerOptions? options = null, string? seed = null)
    {
        using var httpClient = CreateHttpClient();
        var sut = new WebCrawlerDataProvider(seed ?? _fixture.BaseUrl, httpClient, options);

        var entries = new List<FileEntry>();
        await foreach (var result in sut.GetFilesAsync(TestContext.Current.CancellationToken))
        {
            // A failed Result would be a crawl error, and silently dropping it is how a crawl that
            // fetched nothing looks identical to a crawl of an empty site.
            Assert.True(result.IsSuccess, $"Crawl yielded an error: {result}");
            entries.Add(result.Value);
        }

        return entries;
    }

    private static string PathOf(FileEntry entry) => new Uri(entry.Id.Value).AbsolutePath.TrimEnd('/');

    /// <summary>Reads the <c>depth</c> tag, asserting rather than assuming it was set.</summary>
    /// <remarks>
    /// <c>FileEntry.Metadata</c> is optional on the record, so a provider that stopped tagging pages
    /// would give a null-reference failure here instead of a legible one. The tag is the only
    /// evidence of traversal order that survives into the entry, which makes its absence worth its
    /// own message.
    /// </remarks>
    /// <param name="entry">The crawled entry.</param>
    /// <returns>The depth tag's value.</returns>
    private static string DepthOf(FileEntry entry)
    {
        Assert.NotNull(entry.Metadata);
        Assert.True(
            entry.Metadata.TryGetValue("depth", out var depth),
            $"{entry.Id.Value} carries no 'depth' tag. Tags are how BFS distance reaches a caller.");

        return depth.ToString() ?? string.Empty;
    }

    [Fact]
    public async Task ItCrawlsTheWholeReachableSiteAndTerminatesOnTheCycle()
    {
        var entries = await CrawlAsync();

        // Four pages: the seed, /a, /b, /a1. Not /private/secret — robots.txt excludes it, and
        // that is asserted on its own below.
        Assert.Equal(4, entries.Count);
        Assert.Equal(
            ["", "/a", "/a1", "/b"],
            entries.Select(PathOf).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task EachPageCarriesTheContentThatWasFetched()
    {
        var entries = await CrawlAsync();
        var pageA = entries.Single(e => string.Equals(PathOf(e), "/a", StringComparison.Ordinal));

        await using var content = await pageA.OpenContentAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(content);
        var html = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // The body text, not merely non-emptiness: the crawler captures HTML at crawl time and
        // replays it from memory, so an empty or wrong capture is the failure mode worth naming.
        Assert.Contains("Page A body text.", html, StringComparison.Ordinal);
        Assert.Equal("a.html", pageA.FileName);
    }

    [Fact]
    public async Task DepthIsTheBreadthFirstDistanceFromTheSeed()
    {
        var entries = await CrawlAsync();
        var depthByPath = entries.ToDictionary(PathOf, DepthOf, StringComparer.Ordinal);

        Assert.Equal("0", depthByPath[""]);
        Assert.Equal("1", depthByPath["/a"]);
        Assert.Equal("1", depthByPath["/b"]);

        // The one that distinguishes breadth-first from depth-first. /a1 is reachable only through
        // /a, so a depth-first walk would still find it — but it would be visited before /b, and
        // the cycle back to the seed means a walk without a visited set would record it at some
        // other depth entirely.
        Assert.Equal("2", depthByPath["/a1"]);
    }

    [Fact]
    public async Task MaxDepthStopsTheTraversalWithoutStoppingTheCrawl()
    {
        var entries = await CrawlAsync(new WebCrawlerOptions { MaxDepth = 1 });

        // Depth 1 means the seed's links are followed but theirs are not: /a1 is out, /a and /b in.
        Assert.Equal(
            ["", "/a", "/b"],
            entries.Select(PathOf).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task MaxPagesCapsTheCrawl()
    {
        var entries = await CrawlAsync(new WebCrawlerOptions { MaxPages = 2 });

        Assert.Equal(2, entries.Count);
    }

    /// <remarks>
    /// Both directions in one place, because either alone is satisfiable by a bug. A crawler that
    /// never fetched <c>/private/secret</c> for an unrelated reason passes the first assertion; one
    /// that ignored <c>robots.txt</c> entirely passes the second.
    /// </remarks>
    [Fact]
    public async Task RobotsTxtIsObeyedByDefaultAndOnlyByDefault()
    {
        var respected = await CrawlAsync();
        Assert.DoesNotContain("/private/secret", respected.Select(PathOf), StringComparer.Ordinal);

        var ignored = await CrawlAsync(new WebCrawlerOptions { RespectRobotsTxt = false });
        Assert.Contains("/private/secret", ignored.Select(PathOf), StringComparer.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// Seeded from <c>localhost</c>, where the page's absolute link to <c>127.0.0.1</c> is a genuinely
    /// different host on the same socket. Asserting on that one URL rather than on a page count: a
    /// count comparison passes for any reason the two crawls differ, and this option has exactly one
    /// job.
    /// </para>
    /// <para>
    /// The first draft seeded from <c>127.0.0.1</c> instead and asserted a single page. That was
    /// backwards — from that seed the absolute link and every relative link resolve to the same host,
    /// so nothing was cross-host and the crawl found all five pages. Worth recording because the
    /// failure is what surfaced the trailing-slash defect below.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SameDomainDecidesWhetherTheCrawlLeavesTheSeedHost()
    {
        var crossHostPage = $"{_crossHostSeed}/a";

        var confined = await CrawlAsync(new WebCrawlerOptions { SameDomain = true });
        Assert.DoesNotContain(crossHostPage, confined.Select(e => e.Id.Value), StringComparer.Ordinal);

        var roaming = await CrawlAsync(new WebCrawlerOptions { SameDomain = false });
        Assert.Contains(crossHostPage, roaming.Select(e => e.Id.Value), StringComparer.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// <b>Was #288, now the regression test for it.</b> <c>ExtractLinks</c> normalised every
    /// discovered link — no fragment, no trailing slash — while the seed was enqueued verbatim. So a
    /// seed of <c>http://host/</c> and the same page reached through a back-link as
    /// <c>http://host</c> were two entries in an ordinal <see cref="HashSet{T}"/>, and the root was
    /// fetched and yielded twice under two <see cref="EntryId"/>s.
    /// </para>
    /// <para>
    /// It needed a site that links back to its own root, which is why no unit test found it — and the
    /// other tests here pass <c>_fixture.BaseUrl</c>, which carries no trailing slash and therefore
    /// already matched the normalised form. Nothing about a trailing slash suggests it matters, which
    /// is exactly why this asserts on both spellings rather than on the tidy one.
    /// </para>
    /// <para>
    /// The fix routes the seed through the same <c>Normalise</c> the links use, so the two spellings
    /// now produce identical crawls. That equality is the assertion: a count check alone would pass
    /// if both spellings became wrong together.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATrailingSlashOnTheSeedDoesNotChangeTheCrawl()
    {
        var withSlash = await CrawlAsync(seed: $"{_fixture.BaseUrl}/");
        var withoutSlash = await CrawlAsync(seed: _fixture.BaseUrl);

        Assert.Equal(
            withoutSlash.Select(e => e.Id.Value).Order(StringComparer.Ordinal),
            withSlash.Select(e => e.Id.Value).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);

        // No page twice, under either spelling. Stated per-crawl so a failure names which one.
        foreach (var (label, crawl) in new[] { ("with slash", withSlash), ("without", withoutSlash) })
        {
            var duplicated = crawl
                .GroupBy(PathOf, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(
                duplicated.Count == 0,
                $"Seed {label}: these paths were crawled more than once — {string.Join(", ", duplicated)}");
        }

        // And the root is present exactly once, under the normalised id rather than the seed as given.
        Assert.Equal(
            [_fixture.BaseUrl],
            withSlash.Where(e => string.Equals(PathOf(e), string.Empty, StringComparison.Ordinal))
                     .Select(e => e.Id.Value),
            StringComparer.Ordinal);
    }
}
