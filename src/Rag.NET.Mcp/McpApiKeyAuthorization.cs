namespace Rag.NET.Mcp;

/// <summary>
/// The authorization decision behind an MCP HTTP transport's <c>X-Api-Key</c> header.
/// <para>
/// <b>This lives in the library, not the tool, deliberately.</b> <c>Rag.NET.Mcp</c> gives a
/// consumer both transports — <c>WithStdioTransport()</c> and, through
/// <see cref="DependencyInjection.McpServerBuilder.Server"/>, the SDK's HTTP one — and used to
/// give them no way to authenticate either. The key comparison was <c>internal</c> to
/// <c>Rag.NET.Mcp.Tool</c>, so anyone building their own server on this package wrote it
/// themselves or went without, and going without is the easier path (issue #88).
/// </para>
/// <para>
/// Only the decision lives here. The middleware that reads the header and returns 401 is ASP.NET
/// Core, which this package deliberately does not reference — wrapping the SDK's
/// <c>WithHttpTransport</c> would drag a web framework into a package that stdio consumers also
/// use. Comparing two strings correctly is the part that is easy to get subtly wrong and worth
/// sharing; calling <c>context.Response.StatusCode = 401</c> is not.
/// </para>
/// </summary>
public static class McpApiKeyAuthorization
{
    /// <summary>The header an MCP HTTP client sends its key in.</summary>
    public const string HeaderName = "X-Api-Key";

    /// <summary>
    /// Whether a request may proceed.
    /// </summary>
    /// <param name="suppliedKey">
    /// The <see cref="HeaderName"/> value from the request, or <see langword="null"/> when the
    /// header was absent.
    /// </param>
    /// <param name="expectedKey">
    /// The configured key, or <see langword="null"/> when none was configured.
    /// </param>
    /// <param name="allowAnonymous">
    /// Whether serving unauthenticated requests was an explicit decision. Only consulted when
    /// <paramref name="expectedKey"/> is absent.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the supplied key matches, or when no key is configured and
    /// <paramref name="allowAnonymous"/> says that was deliberate.
    /// <para>
    /// <b>No configured key and no explicit opt-out returns <see langword="false"/>.</b> The
    /// previous arrangement skipped registering the middleware entirely in that case, so the
    /// endpoint served ingest, retrieve and ask to anyone who could reach the port — and the tool
    /// binds every interface, not just loopback. That is the same fail-open shape #92 fixed in
    /// <c>Rag.NET.Api</c>, and refusing here is what makes "nobody configured auth" look
    /// different from "auth passed".
    /// </para>
    /// </returns>
    public static bool IsAuthorized(string? suppliedKey, string? expectedKey, bool allowAnonymous)
    {
        if (string.IsNullOrEmpty(expectedKey))
        {
            return allowAnonymous;
        }

        // Ordinal and case-sensitive: an API key is a secret, not a display string. Length is
        // compared first by string.Equals, which is fine — the key's length is not the secret.
        return suppliedKey is not null
            && string.Equals(suppliedKey, expectedKey, StringComparison.Ordinal);
    }
}
