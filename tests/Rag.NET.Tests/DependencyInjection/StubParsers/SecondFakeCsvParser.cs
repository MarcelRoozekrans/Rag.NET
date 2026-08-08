using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Tests.DependencyInjection.StubParsers;

/// <summary>
/// A second, independent parser claiming <c>text/csv</c> — registered without <c>replaces:</c> to
/// prove that <c>AddParser&lt;TParser&gt;(replaces:)</c>'s escape hatch does not disable the
/// conflict guard for registrations that do not use it.
/// </summary>
internal sealed class SecondFakeCsvParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<DocumentSection>();
}
