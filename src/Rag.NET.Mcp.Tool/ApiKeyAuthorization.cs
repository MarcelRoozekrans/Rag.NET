namespace Rag.NET.Mcp.Tool;

/// <summary>
/// The authorization decision behind the HTTP transport's <c>X-Api-Key</c> middleware: does a
/// supplied header value match the configured key.
/// </summary>
/// <remarks>
/// Pulled out of the middleware lambda in <c>Program.cs</c> for the same reason as
/// <see cref="ProgramArguments"/>: it is the one piece of that middleware that is a pure
/// computation rather than an ASP.NET Core pipeline effect, so it is the part a unit test can
/// assert on honestly. What this does <em>not</em> cover: whether the middleware is actually
/// registered only when an API key is configured, whether it runs before <c>MapMcp</c>, or
/// whether a real request without the header truly receives HTTP 401 — those are the transport
/// actually working, and exercising them means running Kestrel. This package's tests assert the
/// decision function; they do not stand up the HTTP host to prove the wiring around it.
/// </remarks>
internal static class ApiKeyAuthorization
{
    /// <summary>
    /// Whether a request supplying <paramref name="suppliedKey"/> may proceed, given the
    /// configured <paramref name="expectedKey"/>.
    /// </summary>
    /// <param name="suppliedKey">
    /// The <c>X-Api-Key</c> header value from the request, or <see langword="null"/> when the
    /// header was absent.
    /// </param>
    /// <param name="expectedKey">The API key configured for this run.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="suppliedKey"/> is present and matches
    /// <paramref name="expectedKey"/> exactly (ordinal, case-sensitive — an API key is a secret,
    /// not a display string); otherwise <see langword="false"/>.
    /// </returns>
    public static bool IsAuthorized(string? suppliedKey, string expectedKey) =>
        suppliedKey is not null && string.Equals(suppliedKey, expectedKey, StringComparison.Ordinal);
}
