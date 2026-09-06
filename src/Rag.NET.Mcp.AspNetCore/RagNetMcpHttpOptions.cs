namespace Rag.NET.Mcp.AspNetCore;

/// <summary>
/// The authentication decision for an MCP HTTP transport.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no safe default here, which is why one is not offered.</b> The MCP tool surface
/// exposes <c>rag_ingest</c> as well as retrieve and ask, so an unauthenticated HTTP host lets
/// anyone who can reach the port write into the corpus that answers your users' questions — and
/// retrieval degrades silently rather than erroring, so nothing surfaces it. Leaving both
/// properties unset is therefore not "the default"; it is an unmade decision, and
/// <c>WithRagNetHttpTransport</c> refuses it.
/// </para>
/// </remarks>
public sealed class RagNetMcpHttpOptions
{
    /// <summary>
    /// The key an MCP HTTP client must send in <see cref="McpApiKeyAuthorization.HeaderName"/>.
    /// </summary>
    /// <remarks>
    /// A shared secret, compared ordinally by <see cref="McpApiKeyAuthorization.IsAuthorized"/> —
    /// the same scheme <c>Rag.NET.Api</c>, its gRPC sibling and <c>ragnet-mcp</c> use. It is not an
    /// identity: every client presenting it is indistinguishable from every other, and there is no
    /// revocation short of changing it.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Serve unauthenticated, deliberately.
    /// </summary>
    /// <remarks>
    /// For a host already behind a gateway that authenticates. It is a real opt-out and stays one —
    /// the point of the guard is to separate "someone decided this" from "nobody thought about it",
    /// not to make the anonymous case impossible.
    /// </remarks>
    public bool AllowAnonymous { get; set; }
}
