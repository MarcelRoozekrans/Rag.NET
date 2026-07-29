using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Email.Tests;

/// <summary>
/// A parser whose <c>ParseAsync</c> throws when it is <i>called</i> rather than when the returned
/// sequence is enumerated. <see cref="IDocumentParser.ParseAsync"/> does not have to be an
/// iterator, and an ordinary method that validates its arguments first fails on this route, so
/// containment has to cover it as well as <c>MoveNextAsync</c>.
/// </summary>
internal sealed class EagerlyThrowingDocumentParser(string contentType) : IDocumentParser
{
    public bool CanParse(string candidate) =>
        candidate.Equals(contentType, StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Refusing '{metadata.FileName}' before enumeration begins.");
}
