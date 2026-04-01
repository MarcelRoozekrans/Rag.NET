using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Rag.NET.Chunking.Templates;

public sealed partial class QAPairsDocumentParser(
    QAPairsChunkingOptions options,
    ILogger<QAPairsDocumentParser>? logger = null) : IDocumentParser
{
    private readonly ILogger<QAPairsDocumentParser> _logger = logger ?? NullLogger<QAPairsDocumentParser>.Instance;

    public bool CanParse(string contentType) =>
        string.Equals(contentType, "text/csv", StringComparison.Ordinal)
            || string.Equals(contentType, "application/vnd.ms-excel", StringComparison.Ordinal)
            || string.Equals(contentType, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.Ordinal)
            || string.Equals(contentType, "application/octet-stream", StringComparison.Ordinal);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(metadata.FileName).ToLowerInvariant();
        if (string.Equals(ext, ".xlsx", StringComparison.Ordinal) || string.Equals(ext, ".xls", StringComparison.Ordinal))
        {
            await foreach (var section in ParseExcelAsync(stream, metadata, cancellationToken).ConfigureAwait(false))
                yield return section;
            yield break;
        }

        await foreach (var section in ParseCsvAsync(stream, metadata, cancellationToken).ConfigureAwait(false))
            yield return section;
    }

    private async IAsyncEnumerable<DocumentSection> ParseCsvAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = options.SkipHeader };
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync().ConfigureAwait(false);
        csv.ReadHeader();

        var headers = csv.HeaderRecord ?? [];
        var questionCol = options.QuestionColumn
            ?? headers.FirstOrDefault(h => QAPairsChunkingOptions.DefaultQuestionColumns
                .Contains(h, StringComparer.OrdinalIgnoreCase));
        var answerCol = options.AnswerColumn
            ?? headers.FirstOrDefault(h => QAPairsChunkingOptions.DefaultAnswerColumns
                .Contains(h, StringComparer.OrdinalIgnoreCase));

        if (questionCol is null || answerCol is null)
            throw new InvalidOperationException(
                $"Cannot resolve question/answer columns. Headers: [{string.Join(", ", headers)}].");

        var index = 0;
        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var question = csv.GetField(questionCol) ?? string.Empty;
            var answer = csv.GetField(answerCol) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(question))
            {
                LogSkippingEmptyQuestion(_logger, index + 1);
                continue;
            }

            if (string.IsNullOrWhiteSpace(answer))
                LogEmptyAnswer(_logger, index + 1);

            yield return new DocumentSection
            {
                Text = question,
                Heading = answer,
                DocumentId = metadata.DocumentId,
                SectionIndex = index++,
            };
        }
    }

    private async IAsyncEnumerable<DocumentSection> ParseExcelAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var rows = sheet.RowsUsed().ToList();
        if (rows.Count < 2) yield break;

        var headerRow = rows[0];
        var headers = headerRow.Cells().Select(c => c.GetString()).ToArray();

        var questionColIdx = FindColumnIndex(headers, options.QuestionColumn, QAPairsChunkingOptions.DefaultQuestionColumns);
        var answerColIdx = FindColumnIndex(headers, options.AnswerColumn, QAPairsChunkingOptions.DefaultAnswerColumns);

        if (questionColIdx < 0 || answerColIdx < 0)
            throw new InvalidOperationException(
                $"Cannot resolve question/answer columns. Headers: [{string.Join(", ", headers)}].");

        var index = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[i];
            var question = row.Cell(questionColIdx + 1).GetString();
            var answer = row.Cell(answerColIdx + 1).GetString();

            if (string.IsNullOrWhiteSpace(question))
            {
                LogSkippingEmptyQuestion(_logger, index + 1);
                continue;
            }

            if (string.IsNullOrWhiteSpace(answer))
                LogEmptyAnswer(_logger, index + 1);

            yield return new DocumentSection
            {
                Text = question,
                Heading = answer,
                DocumentId = metadata.DocumentId,
                SectionIndex = index++,
            };
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static int FindColumnIndex(string[] headers, string? preferred, string[] defaults)
    {
        if (preferred is not null)
            return Array.FindIndex(headers, h => h.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        foreach (var d in defaults)
        {
            var i = Array.FindIndex(headers, h => h.Equals(d, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping row {Row} — empty question column.")]
    private static partial void LogSkippingEmptyQuestion(ILogger logger, int row);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Row {Row} has an empty answer column — chunk will be stored with no answer text.")]
    private static partial void LogEmptyAnswer(ILogger logger, int row);
}
