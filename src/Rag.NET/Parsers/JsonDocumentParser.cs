using System.Runtime.CompilerServices;
using System.Text.Json;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers;

public sealed class JsonDocumentParser : IDocumentParser
{
    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    public bool CanParse(string contentType) =>
        contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (stream.Length == 0)
        {
            yield break;
        }

        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        int sectionIndex = 0;

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return new DocumentSection
                {
                    Text = JsonSerializer.Serialize(element, s_writeOptions),
                    DocumentId = metadata.DocumentId,
                    SectionIndex = sectionIndex++,
                };
            }
        }
        else
        {
            yield return new DocumentSection
            {
                Text = JsonSerializer.Serialize(document.RootElement, s_writeOptions),
                DocumentId = metadata.DocumentId,
                SectionIndex = 0,
            };
        }
    }
}
