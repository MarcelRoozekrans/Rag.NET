using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Tests.DependencyInjection.StubParsers;

/// <summary>
/// Stands in for a parser that deliberately overrides the built-in <c>CsvDocumentParser</c> via
/// <c>AddParser&lt;TParser&gt;(replaces:)</c>. Claims <c>text/csv</c>, same as the parser it
/// replaces — the collision the override exists to resolve.
/// </summary>
internal sealed class FakeCsvParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<DocumentSection>();
}
