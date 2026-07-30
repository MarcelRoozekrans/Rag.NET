using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Archive.Tests;

/// <summary>
/// A parser that claims the content types it is given, emits one section holding the entry's full
/// text, and records the <see cref="DocumentMetadata"/> it was handed for each.
/// </summary>
/// <remarks>
/// It reads the stream to the end deliberately. The bounds live in <see cref="LimitedReadStream"/>,
/// which only counts bytes something actually pulls through it — a fake that ignored the stream would
/// let every bomb test pass by never reading the bomb.
/// </remarks>
/// <param name="contentTypes">The content types this parser answers to.</param>
internal sealed class RecordingParser(params string[] contentTypes) : IDocumentParser
{
    /// <summary>The metadata of every entry dispatched here, in order.</summary>
    public List<DocumentMetadata> ReceivedMetadata { get; } = [];

    public bool CanParse(string contentType) =>
        Array.Exists(contentTypes, t => t.Equals(contentType, StringComparison.OrdinalIgnoreCase));

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
