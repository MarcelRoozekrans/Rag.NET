using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Rag.NET.Chunking.Templates;

public sealed partial class EmailTemplateDocumentParser(
    EmailChunkingOptions options,
    ILogger<EmailTemplateDocumentParser>? logger = null) : IDocumentParser
{
    private static readonly string[] TextExtensions = [".txt", ".md", ".csv", ".tsv"];
    private readonly ILogger<EmailTemplateDocumentParser> _logger = logger ?? NullLogger<EmailTemplateDocumentParser>.Instance;

    public bool CanParse(string contentType) =>
        string.Equals(contentType, "message/rfc822", StringComparison.Ordinal);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        MimeMessage message;
        try
        {
            message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse .eml file '{metadata.FileName}'.", ex);
        }

        var index = 0;

        if (options.IncludeHeaders)
        {
            var headerText = new StringBuilder();
            headerText.AppendLine($"From: {message.From}");
            headerText.AppendLine($"To: {message.To}");
            headerText.AppendLine($"Subject: {message.Subject}");
            headerText.AppendLine($"Date: {message.Date.ToString("R")}");

            yield return new DocumentSection
            {
                Text = headerText.ToString().Trim(),
                Heading = "headers",
                DocumentId = metadata.DocumentId,
                SectionIndex = index++,
            };
        }

        var bodyText = GetBodyText(message);
        if (!string.IsNullOrWhiteSpace(bodyText))
        {
            yield return new DocumentSection
            {
                Text = bodyText,
                Heading = "body",
                DocumentId = metadata.DocumentId,
                SectionIndex = index++,
            };
        }

        if (options.IncludeAttachments)
        {
            await foreach (var section in ParseAttachmentsAsync(message, metadata, index, cancellationToken).ConfigureAwait(false))
                yield return section;
        }
    }

    private async IAsyncEnumerable<DocumentSection> ParseAttachmentsAsync(
        MimeMessage message,
        DocumentMetadata metadata,
        int startIndex,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = startIndex;
        foreach (var attachment in message.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attachment is not MimePart part) continue;

            var ext = Path.GetExtension(part.FileName ?? string.Empty).ToLowerInvariant();
            if (Array.IndexOf(TextExtensions, ext) < 0)
            {
                LogSkippingBinaryAttachment(_logger, part.FileName ?? "(unknown)");
                continue;
            }

            if (part.Content is null) continue;

            using var ms = new MemoryStream();
            await part.Content.DecodeToAsync(ms, cancellationToken).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(ms.ToArray());

            if (string.IsNullOrWhiteSpace(text)) continue;

            yield return new DocumentSection
            {
                Text = text,
                Heading = $"attachment:{part.FileName}",
                DocumentId = metadata.DocumentId,
                SectionIndex = index++,
            };
        }
    }

    private static string GetBodyText(MimeMessage message)
    {
        if (message.TextBody is { } plain)
            return plain;

        if (message.HtmlBody is { } html)
        {
            var stripped = HtmlTagRegex().Replace(html, " ");
            stripped = stripped
                .Replace("&amp;", "&", StringComparison.Ordinal)
                .Replace("&lt;", "<", StringComparison.Ordinal)
                .Replace("&gt;", ">", StringComparison.Ordinal)
                .Replace("&nbsp;", " ", StringComparison.Ordinal);
            return WhitespaceCollapseRegex().Replace(stripped, " ").Trim();
        }

        return string.Empty;
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespaceCollapseRegex();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping binary attachment '{FileName}' — binary attachments are not inlined.")]
    private static partial void LogSkippingBinaryAttachment(ILogger logger, string fileName);
}
