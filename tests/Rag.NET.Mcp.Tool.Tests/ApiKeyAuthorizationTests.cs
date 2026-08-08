using Rag.NET.Mcp.Tool;
using Xunit;

namespace Rag.NET.Mcp.Tool.Tests;

/// <summary>
/// Covers the authorization decision behind the HTTP transport's <c>X-Api-Key</c> middleware.
/// Security-adjacent by design: a middleware that stops rejecting bad keys fails silently — the
/// request simply succeeds — so this asserts the decision both ways, not just the happy path.
/// </summary>
/// <remarks>
/// This does not stand up the ASP.NET Core pipeline: no <c>TestServer</c>, no real HTTP request.
/// It asserts <see cref="ApiKeyAuthorization.IsAuthorized"/> directly, which is the pure decision
/// the middleware lambda in <c>Program.cs</c> delegates to. Whether that middleware is actually
/// registered only when <c>--api-key</c> is supplied, and whether a real unauthorized request
/// truly gets HTTP 401 before reaching <c>MapMcp</c>, is the wiring around the decision — not
/// covered here; see <see cref="ApiKeyAuthorization"/>'s remarks.
/// </remarks>
public sealed class ApiKeyAuthorizationTests
{
    [Fact]
    public void IsAuthorized_SuppliedKeyMatchesExpectedKey_ReturnsTrue()
    {
        var authorized = ApiKeyAuthorization.IsAuthorized("secret", "secret");

        Assert.True(authorized);
    }

    [Fact]
    public void IsAuthorized_SuppliedKeyIsNull_ReturnsFalse()
    {
        // The header was absent entirely — TryGetValue's false branch, in Program.cs.
        var authorized = ApiKeyAuthorization.IsAuthorized(null, "secret");

        Assert.False(authorized);
    }

    [Fact]
    public void IsAuthorized_SuppliedKeyDiffersFromExpectedKey_ReturnsFalse()
    {
        var authorized = ApiKeyAuthorization.IsAuthorized("wrong-secret", "secret");

        Assert.False(authorized);
    }

    [Fact]
    public void IsAuthorized_SuppliedKeyDiffersOnlyByCase_ReturnsFalse()
    {
        // Ordinal, case-sensitive comparison: an API key is a secret, not a display string that
        // should tolerate a differently-cased match.
        var authorized = ApiKeyAuthorization.IsAuthorized("SECRET", "secret");

        Assert.False(authorized);
    }

    [Fact]
    public void IsAuthorized_SuppliedKeyIsEmptyAndExpectedKeyIsNot_ReturnsFalse()
    {
        var authorized = ApiKeyAuthorization.IsAuthorized(string.Empty, "secret");

        Assert.False(authorized);
    }
}
