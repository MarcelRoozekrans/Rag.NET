using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Tests.DependencyInjection.StubParsers;

/// <summary>
/// Stands in for the parser a user writes when the built-in <c>TextDocumentParser</c> is not what
/// they want for <c>text/plain</c> — the case that used to slip past the conflict guard entirely.
/// </summary>
internal sealed class PlainTextStubParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<DocumentSection>();
}
