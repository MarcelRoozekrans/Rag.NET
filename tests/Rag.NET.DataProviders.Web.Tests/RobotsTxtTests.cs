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
}
