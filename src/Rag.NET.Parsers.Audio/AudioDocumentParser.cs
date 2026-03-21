using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Audio;

public class AudioDocumentParser : IDocumentParser
{
    private static readonly HashSet<string> SupportedTypes =
    [
        "audio/wav",
        "audio/mpeg",
        "audio/flac",
        "audio/ogg",
        "audio/mp4",
    ];

    private readonly AudioParserOptions _options;

    public AudioDocumentParser(AudioParserOptions options)
    {
        _options = options;
    }

    public bool CanParse(string contentType) => SupportedTypes.Contains(contentType);

    public IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Full implementation in Task 3.");
    }
}
