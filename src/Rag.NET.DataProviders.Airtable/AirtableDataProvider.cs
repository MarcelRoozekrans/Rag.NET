using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirtableApiClient;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Airtable;

/// <summary>
/// Enumerates Airtable records as Markdown documents with attachment support.
/// <para>
/// Each record is emitted as a Markdown <see cref="FileHandle"/> (first field as title,
/// remaining fields in a table or as sections for long text). Attachment fields produce
/// additional <see cref="FileHandle"/> entries whose content is downloaded at read time
/// because Airtable attachment URLs are signed and expire.
/// </para>
/// <para>
/// Delta support uses <c>LAST_MODIFIED_TIME()&gt;'{DeltaToken}'</c> when
/// <see cref="AirtableOptions.LastModifiedFieldName"/> is set.
/// </para>
/// </summary>
public sealed class AirtableDataProvider : FileContentProviderBase
{
    private readonly IAirtableClient _client;
    private readonly HttpClient _http;
    private readonly AirtableOptions _options;

    internal AirtableDataProvider(IAirtableClient client, HttpClient http, AirtableOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(http);
        _client  = client;
        _http    = http;
        _options = options;
    }

    /// <inheritdoc />
    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? offset = null;
        var formula = BuildFilterFormula();

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _client.ListRecordsAsync(
                _options.TableName, offset, formula, _options.View, cancellationToken)
                .ConfigureAwait(false);

            if (!response.Success)
            {
                throw response.AirtableApiError is not null
                    ? response.AirtableApiError
                    : new InvalidOperationException("Airtable API returned an unsuccessful response.");
            }

            if (response.Records is not null)
            {
                foreach (var record in response.Records)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Emit the markdown handle for the record itself.
                    yield return Result<FileHandle, RagError>.Success(ToMarkdownHandle(record));

                    // Emit separate handles for each attachment in every attachment field.
                    foreach (var handle in GetAttachmentHandles(record))
                        yield return Result<FileHandle, RagError>.Success(handle);
                }
            }

            offset = response.Offset;
        }
        while (offset is not null);
    }

    private string? BuildFilterFormula()
    {
        if (_options.LastModifiedFieldName is null || _options.DeltaToken is null)
            return null;

        // Airtable formula: LAST_MODIFIED_TIME()>'2026-03-01T00:00:00.000Z'
        return $"LAST_MODIFIED_TIME()>'{_options.DeltaToken}'";
    }

    private static FileHandle ToMarkdownHandle(AirtableRecord record)
    {
        var markdown = ToMarkdown(record);
        var etag = ComputeETag(record);

        return new FileHandle(
            Id:               record.Id ?? string.Empty,
            FileName:         $"{FileNameSanitizer.Sanitize(
                GetRecordTitle(record), $"record-{record.Id}")}.md",
            ETag:             etag,
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
    }

    private IEnumerable<FileHandle> GetAttachmentHandles(AirtableRecord record)
    {
        if (record.Fields is null) yield break;

        foreach (var (fieldName, value) in record.Fields)
        {
            var attachments = TryParseAttachments(value);
            if (attachments is null) continue;

            for (int i = 0; i < attachments.Count; i++)
            {
                var att = attachments[i];
                var url = att.Url ?? string.Empty;
                var filename = att.Filename ?? $"attachment_{i}";
                var recordId = record.Id ?? string.Empty;
                var id = $"{recordId}/{fieldName}/{filename}";

                // Capture url for the closure — attachment URLs expire, so we download at call time.
                yield return new FileHandle(
                    Id:               id,
                    FileName:         filename,
                    ETag:             att.Id ?? string.Empty,
                    OpenContentAsync: ct => DownloadAttachmentAsync(url, ct));
            }
        }
    }

    private async Task<Stream> DownloadAttachmentAsync(string url, CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ToMarkdown(AirtableRecord record)
    {
        if (record.Fields is null || record.Fields.Count == 0)
            return $"# {record.Id}";

        var sb = new StringBuilder();
        var fields = record.Fields.ToList();
        var title = GetRecordTitle(record);
        sb.AppendLine($"# {title}");
        sb.AppendLine();

        // Separate table fields from long-text fields and attachment fields.
        var tableFields = new List<KeyValuePair<string, string>>();

        for (int i = 0; i < fields.Count; i++)
        {
            var (name, value) = fields[i];

            // Skip attachment fields in the markdown body — they are emitted as separate handles.
            if (TryParseAttachments(value) is not null) continue;

            var stringValue = FormatFieldValue(value);

            // Treat multi-line text as a section rather than a table row.
            if (stringValue.Contains('\n', StringComparison.Ordinal))
            {
                // Flush any accumulated table rows first.
                FlushTable(sb, tableFields);

                sb.AppendLine($"## {name}");
                sb.AppendLine();
                sb.AppendLine(stringValue);
                sb.AppendLine();
            }
            else
            {
                // First field was already used as title — skip it in the table.
                if (i == 0) continue;
                tableFields.Add(new KeyValuePair<string, string>(name, stringValue));
            }
        }

        FlushTable(sb, tableFields);

        return sb.ToString().TrimEnd();
    }

    private static void FlushTable(StringBuilder sb, List<KeyValuePair<string, string>> rows)
    {
        if (rows.Count == 0) return;

        sb.AppendLine("| Field | Value |");
        sb.AppendLine("| --- | --- |");
        for (int i = 0; i < rows.Count; i++)
            sb.AppendLine($"| {EscapePipe(rows[i].Key)} | {EscapePipe(rows[i].Value)} |");
        sb.AppendLine();

        rows.Clear();
    }

    private static string EscapePipe(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string GetRecordTitle(AirtableRecord record)
    {
        if (record.Fields is not null && record.Fields.Count > 0)
        {
            var first = record.Fields.First();
            var titleValue = FormatFieldValue(first.Value);
            if (!string.IsNullOrWhiteSpace(titleValue))
                return titleValue.ReplaceLineEndings(" ");
        }

        return record.Id ?? string.Empty;
    }

    private static string FormatFieldValue(object? value)
    {
        if (value is null) return string.Empty;
        if (value is JsonElement je) return FormatJsonElement(je);
        return value.ToString() ?? string.Empty;
    }

    private static string FormatJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String  => element.GetString() ?? string.Empty,
        JsonValueKind.Number  => element.GetRawText(),
        JsonValueKind.True    => "true",
        JsonValueKind.False   => "false",
        JsonValueKind.Null    => string.Empty,
        _                     => element.GetRawText()
    };

    private static List<AirtableAttachment>? TryParseAttachments(object? value)
    {
        // The Airtable SDK deserializes attachment fields as JArray/JsonElement arrays of objects
        // with "url" and "filename" properties.
        if (value is not JsonElement element) return null;
        if (element.ValueKind != JsonValueKind.Array) return null;

        var result = new List<AirtableAttachment>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            if (!item.TryGetProperty("url", out var urlProp)) return null;

            var url = urlProp.GetString();
            if (string.IsNullOrEmpty(url)) return null;

            var filename = item.TryGetProperty("filename", out var fProp)
                ? fProp.GetString() : null;
            var id = item.TryGetProperty("id", out var idProp)
                ? idProp.GetString() : null;

            result.Add(new AirtableAttachment { Url = url, Filename = filename!, Id = id! });
        }

        return result.Count > 0 ? result : null;
    }

    private static string ComputeETag(AirtableRecord record)
    {
        if (record.Fields is null || record.Fields.Count == 0)
            return record.Id ?? string.Empty;

        var json = JsonSerializer.Serialize(record.Fields);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hash);
    }
}
