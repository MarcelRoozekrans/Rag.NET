namespace Rag.NET.Models;

/// <summary>
/// Discriminated union of all errors that can occur at the Rag.NET facade boundary.
/// Pattern-match with a switch expression to handle specific subtypes.
/// </summary>
public abstract record RagError
{
    /// <summary>One or more input validation rules failed.</summary>
    public sealed record ValidationFailed(IReadOnlyList<ValidationFailure> Failures) : RagError;

    /// <summary>No registered IDocumentParser handles the content type.</summary>
    public sealed record NoParserFound(string ContentType) : RagError;

    /// <summary>An exception was thrown by a storage operation.</summary>
    public sealed record StorageFailed(Exception Inner) : RagError;

    /// <summary>The ingestion stream is not readable.</summary>
    public sealed record NonSeekableStream() : RagError;

    /// <summary>
    /// An HTTP call to an external data provider failed. A response <b>was</b> exchanged;
    /// <paramref name="StatusCode"/> describes that HTTP outcome.
    /// <para>
    /// Usually the status is the one the server returned verbatim. Connectors may also use a
    /// <b>synthetic</b> status when the server answered but the body was unusable — the
    /// convention is <see cref="System.Net.HttpStatusCode.NoContent"/> for a success response
    /// carrying no payload the connector can proceed with (an empty page, an unresolvable id).
    /// Callers must therefore not treat the status as provenance from the origin server; treat
    /// it as this library's classification of the HTTP outcome.
    /// </para>
    /// </summary>
    /// <param name="StatusCode">
    /// The HTTP status describing the outcome — the server's own, or a synthetic status per
    /// the convention above.
    /// </param>
    /// <param name="Content">The response body or an explanatory message, if any.</param>
    public sealed record HttpFailed(System.Net.HttpStatusCode StatusCode, string? Content) : RagError;

    /// <summary>
    /// A call to an external service failed <b>before any HTTP response was received</b> —
    /// DNS resolution, TLS handshake, socket reset, connection timeout, client-side request
    /// timeout, or token acquisition.
    /// <para>
    /// Distinct from <see cref="HttpFailed"/>, where an HTTP exchange did occur and a status
    /// describes its outcome: there is no status here at all, because the server never
    /// answered. Distinct from <see cref="StorageFailed"/>, which covers failures of a
    /// <c>IVectorStore</c>/persistence operation rather than a network transport.
    /// </para>
    /// <para>
    /// Caller cancellation is never reported as a transport failure — an
    /// <see cref="OperationCanceledException"/> raised by the caller's token always
    /// propagates.
    /// </para>
    /// </summary>
    /// <param name="Inner">The transport-level exception that was caught.</param>
    public sealed record TransportFailed(Exception Inner) : RagError;
}
