using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Email.Tests;

/// <summary>
/// Fake parser claiming <c>text/plain</c>; emits one section with the stream's full text
/// and records each <see cref="DocumentMetadata"/> it receives so tests can pin the
/// attachment-metadata contract.
/// </summary>
internal sealed class FakeTextParser : IDocumentParser
{
    public List<DocumentMetadata> ReceivedMetadata { get; } = [];

    public bool CanParse(string contentType) =>
        contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ReceivedMetadata.Add(metadata);
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        yield return new DocumentSection
        {
            Text = text,
            DocumentId = metadata.DocumentId,
            SectionIndex = 0,
        };
    }
}
