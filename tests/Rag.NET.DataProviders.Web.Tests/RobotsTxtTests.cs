using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// <see cref="RobotsTxt"/> against RFC 9309 section 2.2 (#927): groups, longest match,
/// <c>*</c> and <c>$</c>, percent-encoding, and <c>Crawl-delay</c>.
/// </summary>
public sealed class RobotsTxtTests
{
    private static bool Blocked(string robots, string path, string? agent = null) =>
        RobotsTxt.Parse(robots, agent).IsDisallowed("https://example.com" + path);

    // ── Groups ──────────────────────────────────────────────────────────────

    [Fact]
    public void ConsecutiveUserAgentLines_FormOneGroup()
    {
        const string robots = "User-agent: a\nUser-agent: *\nDisallow: /x\n";
        Assert.True(Blocked(robots, "/x"));
    }

    [Fact]
    public void NamedGroup_ReplacesTheStarGroup()
    {
        const string robots = "User-agent: *\nDisallow: /a\n\nUser-agent: MyBot\nDisallow: /b\n";

        Assert.False(Blocked(robots, "/a", "mybot"));
        Assert.True(Blocked(robots, "/b", "mybot"));
        Assert.True(Blocked(robots, "/a"));
        Assert.False(Blocked(robots, "/b"));
    }

    [Fact]
    public void GroupsForTheSameAgent_AreMerged()
    {
        const string robots = "User-agent: *\nDisallow: /foo\n\nUser-agent: *\nDisallow: /baz\n";

        Assert.True(Blocked(robots, "/foo"));
        Assert.True(Blocked(robots, "/baz"));
    }

    [Fact]
    public void NoApplicableGroup_AllowsEverything()
    {
        const string robots = "User-agent: OtherBot\nDisallow: /\n";
        Assert.False(Blocked(robots, "/anything"));
    }

    [Fact]
    public void RulesBeforeTheFirstUserAgent_AreIgnored()
    {
        const string robots = "Disallow: /x\nUser-agent: *\nDisallow: /y\n";

        Assert.False(Blocked(robots, "/x"));
        Assert.True(Blocked(robots, "/y"));
    }

    [Fact]
    public void SitemapLine_DoesNotEndTheGroup()
    {
        const string robots = "User-agent: *\nSitemap: https://example.com/s.xml\nDisallow: /x\n";
        Assert.True(Blocked(robots, "/x"));
    }

    // ── Rules ───────────────────────────────────────────────────────────────

    [Fact]
    public void LongestMatchWins()
    {
        const string robots = "User-agent: *\nDisallow: /core/\nAllow: /core/public/\n";

        Assert.False(Blocked(robots, "/core/public/a"));
        Assert.True(Blocked(robots, "/core/secret"));
    }

    [Fact]
    public void AllowWinsATie()
    {
        const string robots = "User-agent: *\nDisallow: /p\nAllow: /p\n";
        Assert.False(Blocked(robots, "/p"));
    }

    [Fact]
    public void MatchingIsCaseSensitive()
    {
        const string robots = "User-agent: *\nDisallow: /Private\n";

        Assert.False(Blocked(robots, "/private"));
        Assert.True(Blocked(robots, "/Private/x"));
    }

    [Fact]
    public void RecordNamesAreCaseInsensitive()
    {
        const string robots = "USER-AGENT: *\ndisallow: /x\n";
        Assert.True(Blocked(robots, "/x"));
    }

    [Fact]
    public void EmptyDisallow_IsNoRule()
    {
        const string robots = "User-agent: *\nDisallow:\n";
        Assert.False(Blocked(robots, "/"));
    }

    [Fact]
    public void RobotsTxtItself_IsAlwaysAllowed()
    {
        const string robots = "User-agent: *\nDisallow: /\n";

        Assert.False(Blocked(robots, "/robots.txt"));
        Assert.True(Blocked(robots, "/a"));
    }

    [Fact]
    public void CommentsAreStripped()
    {
        const string robots = "User-agent: * # all\nDisallow: /x # private\n";

        Assert.True(Blocked(robots, "/x"));
        Assert.False(Blocked(robots, "/y"));
    }

    [Fact]
    public void QueryIsPartOfThePath()
    {
        const string robots = "User-agent: *\nDisallow: /search?q=\n";

        Assert.True(Blocked(robots, "/search?q=cats"));
        Assert.False(Blocked(robots, "/search"));
    }

    [Fact]
    public void CrLfAndBom_Parse()
    {
        const string robots = "﻿User-agent: *\r\nDisallow: /x\r\n";
        Assert.True(Blocked(robots, "/x"));
    }

    // ── * and $ ─────────────────────────────────────────────────────────────

    [Fact]
    public void StarMatchesAnyRun()
    {
        const string robots = "User-agent: *\nDisallow: /*.pdf\n";

        Assert.True(Blocked(robots, "/a/b.pdf"));
        Assert.True(Blocked(robots, "/a.pdf?x=1"));
        Assert.False(Blocked(robots, "/a.html"));
    }

    [Fact]
    public void DollarAnchorsTheEnd()
    {
        const string robots = "User-agent: *\nDisallow: /*.pdf$\n";

        Assert.True(Blocked(robots, "/a.pdf"));
        Assert.False(Blocked(robots, "/a.pdf?x=1"));
    }

    [Fact]
    public void DrupalStyleAllow()
    {
        const string robots = "User-agent: *\nDisallow: /core/\nAllow: /core/*.css$\n";

        Assert.False(Blocked(robots, "/core/misc/a.css"));
        Assert.True(Blocked(robots, "/core/misc/a.js"));
    }

    [Fact]
    public void DollarInsideAPattern_IsLiteral()
    {
        const string robots = "User-agent: *\nDisallow: /a$b\n";

        Assert.True(Blocked(robots, "/a$b"));
        Assert.False(Blocked(robots, "/a"));
    }

    [Fact]
    public void LongestMatchCountsPatternOctets()
    {
        const string robots = "User-agent: *\nDisallow: /*\nAllow: /page\n";

        Assert.False(Blocked(robots, "/page"));
        Assert.True(Blocked(robots, "/other"));
    }

    [Fact]
    public void ManyStars_FinishQuickly()
    {
        const string robots = "User-agent: *\nDisallow: /*a*a*a*a*a*a*a*a*b\n";
        var path = "/" + new string('a', 2000);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var blocked = Blocked(robots, path);

        Assert.False(blocked);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Took {stopwatch.Elapsed.TotalMilliseconds} ms.");
    }

    // ── Percent-encoding ────────────────────────────────────────────────────

    [Fact]
    public void PercentEncodedUnreserved_IsDecoded()
    {
        Assert.True(Blocked("User-agent: *\nDisallow: /foo/bar/baz\n", "/foo/bar/%62%61%7A"));
        Assert.True(Blocked("User-agent: *\nDisallow: /foo/bar/%62az\n", "/foo/bar/baz"));
    }

    [Fact]
    public void PercentEncodedReserved_StaysEncoded()
    {
        Assert.False(Blocked("User-agent: *\nDisallow: /a/b\n", "/a%2Fb"));
    }

    [Fact]
    public void NonAsciiInRule_IsEncoded()
    {
        Assert.True(Blocked("User-agent: *\nDisallow: /foo/bar/\u30C4\n", "/foo/bar/%E3%83%84"));
        Assert.True(Blocked("User-agent: *\nDisallow: /foo/bar/%e3%83%84\n", "/foo/bar/%E3%83%84"));
    }

    [Fact]
    public void EncodedStarInRule_MatchesALiteralStar()
    {
        const string robots = "User-agent: *\nDisallow: /file-%2A.html\n";

        Assert.True(Blocked(robots, "/file-*.html"));
        Assert.False(Blocked(robots, "/file-x.html"));
    }

    // ── Product token and Crawl-delay ───────────────────────────────────────

    [Fact]
    public void ProductTokenOf_TakesTheFirstProductName()
    {
        using var named = new HttpClient();
        named.DefaultRequestHeaders.UserAgent.ParseAdd("MyBot/1.0 (+https://example.com/bot)");
        using var none = new HttpClient();
        using var invalid = new HttpClient();
        invalid.DefaultRequestHeaders.UserAgent.ParseAdd("My.Bot/1.0");

        Assert.Equal("MyBot", RobotsTxt.ProductTokenOf(named));
        Assert.Null(RobotsTxt.ProductTokenOf(none));
        Assert.Null(RobotsTxt.ProductTokenOf(invalid));
    }

    [Fact]
    public void UserAgentValueWithVersion_StillNames()
    {
        const string robots = "User-agent: MyBot/2.0\nDisallow: /b\n";
        Assert.True(Blocked(robots, "/b", "MyBot"));
    }

    [Fact]
    public void CrawlDelay_IsReadFromTheApplicableGroup()
    {
        // Each group has a rule: Crawl-delay alone does not end a run of user-agent lines.
        const string robots =
            "User-agent: *\nDisallow: /a\nCrawl-delay: 5\n\nUser-agent: MyBot\nDisallow: /b\nCrawl-delay: 0.5\n";

        Assert.Equal(TimeSpan.FromSeconds(0.5), RobotsTxt.Parse(robots, "MyBot").CrawlDelay);
        Assert.Equal(TimeSpan.FromSeconds(5), RobotsTxt.Parse(robots, null).CrawlDelay);
    }

    [Fact]
    public void CrawlDelay_LargerOfTwoCounts()
    {
        const string robots = "User-agent: *\nCrawl-delay: 2\nCrawl-delay: 3\n";
        Assert.Equal(TimeSpan.FromSeconds(3), RobotsTxt.Parse(robots, null).CrawlDelay);
    }

    [Fact]
    public void CrawlDelay_InvalidIsIgnored()
    {
        Assert.Null(RobotsTxt.Parse("User-agent: *\nCrawl-delay: soon\n", null).CrawlDelay);
        Assert.Null(RobotsTxt.Parse("User-agent: *\nCrawl-delay: -1\n", null).CrawlDelay);
    }

    /// <remarks>A huge value is honoured as "as slow as a delay can be", not thrown on:
    /// <see cref="TimeSpan.FromSeconds(double)"/> overflows past about 29,000 years and
    /// <see cref="Task.Delay(TimeSpan)"/> refuses more than about 49 days.</remarks>
    [Fact]
    public void CrawlDelay_Huge_IsCappedAtTheLongestDelay()
    {
        var delay = RobotsTxt.Parse("User-agent: *\nCrawl-delay: 1e300\n", null).CrawlDelay;

        Assert.Equal(RobotsTxt.LongestCrawlDelay, delay);
        Assert.True(RobotsTxt.LongestCrawlDelay >= TimeSpan.FromDays(49));
    }

    // ── Review fixes ────────────────────────────────────────────────────────

    /// <remarks>Crawl-delay is not an RFC 9309 record, so by 2.2.4 it must not split a run of
    /// user-agent lines into two groups.</remarks>
    [Fact]
    public void CrawlDelay_DoesNotEndARunOfUserAgentLines()
    {
        const string robots = "User-agent: MyBot\nCrawl-delay: 5\nUser-agent: OtherBot\nDisallow: /private\n";

        Assert.True(Blocked(robots, "/private", "MyBot"));
        Assert.Equal(TimeSpan.FromSeconds(5), RobotsTxt.Parse(robots, "OtherBot").CrawlDelay);
    }

    [Fact]
    public void BareCarriageReturns_EndLines()
    {
        Assert.True(Blocked("User-agent: *\rDisallow: /p\r", "/p"));
    }

    [Fact]
    public void StarFollowedByAName_IsNotTheStarGroup()
    {
        const string robots = "User-agent: *bot\nDisallow: /\n";
        Assert.False(Blocked(robots, "/a"));
    }

    [Fact]
    public void StarFollowedByAComment_IsTheStarGroup()
    {
        Assert.True(Blocked("User-agent: *# everyone\nDisallow: /a\n", "/a"));
    }
}
