using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Excel;

/// <summary>
/// A stand-in for <c>Rag.NET.Parsers.Office</c>'s real <c>ExcelDocumentParser</c>, minted under its
/// exact namespace and name so <c>UseQAPairsChunking()</c>'s by-name replacement
/// (<see cref="Rag.NET.Chunking.Templates.Tests.QAPairsParserReplacementTests"/> uses it) matches it
/// the same way it would the real type — without this test project taking a dependency on the
/// Office package.
/// </summary>
internal sealed class ExcelDocumentParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<DocumentSection>();
}
