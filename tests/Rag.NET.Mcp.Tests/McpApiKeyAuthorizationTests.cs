using Rag.NET.Mcp;
using Xunit;

namespace Rag.NET.Mcp.Tests;

/// <summary>
/// The MCP HTTP transport's authorization decision.
/// <para>
/// Moved here from <c>Rag.NET.Mcp.Tool</c> with the code it tests. It was <c>internal</c> to the
/// executable, so a consumer building their own server on <c>Rag.NET.Mcp</c> — which hands out
/// both transports — had no way to authenticate either and wrote it themselves or went without
/// (issue #88).
/// </para>
/// <para>
/// The no-key cases matter most. The tool previously registered its auth middleware only when a
/// key was configured, so <c>--transport http</c> with no <c>--api-key</c> served ingest,
/// retrieve and ask to anyone who could reach the port, on every interface. These pin that "no
/// key configured" now refuses unless refusing was explicitly waived.
/// </para>
/// </summary>
public class McpApiKeyAuthorizationTests
{
    [Fact]
    public void MatchingKeyIsAuthorized()
    {
        Assert.True(McpApiKeyAuthorization.IsAuthorized("secret", "secret", allowAnonymous: false));
    }

    [Fact]
    public void WrongKeyIsRefused()
    {
        Assert.False(McpApiKeyAuthorization.IsAuthorized("wrong", "secret", allowAnonymous: false));
    }

    [Fact]
    public void MissingHeaderIsRefused()
    {
        Assert.False(McpApiKeyAuthorization.IsAuthorized(null, "secret", allowAnonymous: false));
    }

    [Fact]
    public void KeyComparisonIsCaseSensitive()
    {
        // A key is a secret, not a display string.
        Assert.False(McpApiKeyAuthorization.IsAuthorized("SECRET", "secret", allowAnonymous: false));
    }

    [Fact]
    public void EmptySuppliedKeyIsRefused()
    {
        Assert.False(McpApiKeyAuthorization.IsAuthorized(string.Empty, "secret", allowAnonymous: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoConfiguredKeyIsRefusedUnlessWaived(string? expectedKey)
    {
        // The defect: previously this state meant the middleware was never registered, so every
        // request was served. Refusing is what makes "nobody configured auth" distinguishable
        // from "auth passed".
        Assert.False(McpApiKeyAuthorization.IsAuthorized("anything", expectedKey, allowAnonymous: false));
        Assert.False(McpApiKeyAuthorization.IsAuthorized(null, expectedKey, allowAnonymous: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoConfiguredKeyIsAllowedWhenWaivedExplicitly(string? expectedKey)
    {
        // Running behind a trusted gateway is legitimate — it just has to be said out loud.
        Assert.True(McpApiKeyAuthorization.IsAuthorized(null, expectedKey, allowAnonymous: true));
    }

    [Fact]
    public void AllowAnonymousDoesNotWeakenAConfiguredKey()
    {
        // Otherwise a stray --allow-anonymous would silently disable a key the operator did set,
        // which is a worse outcome than refusing to start.
        Assert.False(McpApiKeyAuthorization.IsAuthorized("wrong", "secret", allowAnonymous: true));
        Assert.True(McpApiKeyAuthorization.IsAuthorized("secret", "secret", allowAnonymous: true));
    }
}
