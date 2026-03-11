using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers;

public sealed class CsvDocumentParser : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(headerLine))
        {
            yield break;
        }

        var headers = ParseCsvLine(headerLine);
        int sectionIndex = 0;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var values = ParseCsvLine(line);
            var pairs = new List<string>(headers.Length);

            for (int i = 0; i < headers.Length; i++)
            {
                var value = i < values.Length ? values[i] : string.Empty;
                pairs.Add($"{headers[i]}: {value}");
            }

            yield return new DocumentSection
            {
                Text = string.Join(" | ", pairs),
                DocumentId = metadata.DocumentId,
                SectionIndex = sectionIndex++,
            };
        }
    }

    private static string[] ParseCsvLine(string line) =>
        line.Split(',').Select(v => v.Trim()).ToArray();
}
