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

    /// <summary>An HTTP call to an external data provider failed.</summary>
    /// <param name="StatusCode">The HTTP status code returned by the server.</param>
    /// <param name="Content">The response body, if any.</param>
    public sealed record HttpFailed(System.Net.HttpStatusCode StatusCode, string? Content) : RagError;
}
