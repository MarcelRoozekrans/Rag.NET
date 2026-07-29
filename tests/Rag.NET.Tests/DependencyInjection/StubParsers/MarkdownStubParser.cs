using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Tests.DependencyInjection.StubParsers;

/// <summary>
/// Claims <c>text/x-markdown</c> and nothing else. The built-in <c>MarkdownDocumentParser</c>
/// answers that alias as well as <c>text/markdown</c>, so a claim declaring only the obvious one
/// would leave this collision undetected.
/// </summary>
internal sealed class MarkdownStubParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("text/x-markdown", StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<DocumentSection>();
}
