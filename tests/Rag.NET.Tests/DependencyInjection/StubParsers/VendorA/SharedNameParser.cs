using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Tests.DependencyInjection.StubParsers.VendorA;

/// <summary>
/// Half of a pair of distinct parsers that share the short name <c>SharedNameParser</c> and differ
/// only by namespace — the shape <c>ParserClaim.ParserTypeName</c>'s <c>FullName</c> keying exists
/// for. Kept in its own namespace-shaped folder because two files in one directory cannot both be
/// named after the type they hold.
/// </summary>
internal sealed class SharedNameParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("application/x-shared-name", StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<DocumentSection>();
}
