using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Tests.DependencyInjection.StubParsers.VendorB;

/// <inheritdoc cref="StubParsers.VendorA.SharedNameParser"/>
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
