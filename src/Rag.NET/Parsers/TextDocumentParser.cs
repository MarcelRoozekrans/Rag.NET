using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Parsers;

[Singleton(As = typeof(IDocumentParser), AllowMultiple = true)]
public sealed class TextDocumentParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        yield return new DocumentSection
        {
            Text = text,
            DocumentId = metadata.DocumentId,
            SectionIndex = 0,
        };
    }
}
