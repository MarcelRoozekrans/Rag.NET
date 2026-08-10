using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Rag.NET.Ingestion.AzureServiceBus.Settlement;
using Rag.NET.Models;

namespace Rag.NET.Ingestion.AzureServiceBus.Messaging;

/// <summary>
/// Turns a Service Bus message body into an <see cref="IngestionJob"/>, using the same JSON
/// contract the HTTP webhook accepts — <c>{ "documentId": "...", "content": "...",
/// "metadata": { ... } }</c> — so one payload shape serves both transports.
/// </summary>
/// <remarks>
/// <para>
/// The other implementation of this contract is <c>GenericWebhookPayloadParser</c> in
/// <c>Rag.NET.Api</c>. It is duplicated rather than shared because that type lives in the API
/// host, which this package must not reference; the two are kept in step by the contract being
/// small and both being tested against the same cases. The one deliberate narrowing: the
/// webhook also accepts a JSON <i>array</i> of documents, and this does not. Settlement here
/// is per message, so a batch that half-succeeds has no way to report itself — one message,
/// one document.
/// </para>
/// <para>
/// The file name is derived through <see cref="FileNameSanitizer"/>. <c>documentId</c> is
/// attacker-controlled on this path exactly as it is on the webhook, and a value of
/// <c>"../../etc/passwd"</c> must not become a file name carrying separators or traversal
/// segments.
/// </para>
/// </remarks>
public static class ServiceBusMessageTranslator
{
    /// <summary>Stem used when <c>documentId</c> sanitizes away to nothing (<c>"..."</c>, <c>"///"</c>).</summary>
    private const string FallbackStem = "document";

    /// <summary>
    /// Translates a received message, honouring its <c>SessionId</c> when the entity is
    /// session-enabled.
    /// </summary>
    /// <param name="message">
    /// The received message. Construct one in tests with <c>ServiceBusModelFactory</c>; the
    /// type has no public constructor.
    /// </param>
    public static MessageTranslation Translate(ServiceBusReceivedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Translate(message.Body, message.SessionId);
    }

    /// <summary>Translates a raw body, optionally under a session id.</summary>
    /// <param name="body">The message body.</param>
    /// <param name="sessionId">
    /// The session the message arrived on, or <see langword="null"/> on a non-session entity.
    /// When present it must equal the body's <c>documentId</c> — a session orders one
    /// document, so a session naming anything else is a producer defect.
    /// </param>
    public static MessageTranslation Translate(BinaryData body, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToMemory());
        }
        catch (JsonException ex)
        {
            return MessageTranslation.PermanentFailure(DeadLetterReasons.MalformedPayload, ex.Message);
        }

        using (document)
        {
            return TranslateRoot(document.RootElement, sessionId);
        }
    }

    private static MessageTranslation TranslateRoot(JsonElement root, string? sessionId)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return MessageTranslation.PermanentFailure(DeadLetterReasons.MalformedPayload,
                "A message carries exactly one document. Settlement is per message, so a batch cannot report a partial failure — send one message per document.");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return MessageTranslation.PermanentFailure(DeadLetterReasons.MalformedPayload,
                $"Expected a JSON object at the root of the body; found {root.ValueKind}.");
        }

        if (!TryGetNonEmptyString(root, "documentId", out var documentId))
        {
            return MessageTranslation.PermanentFailure(DeadLetterReasons.MissingRequiredField,
                "'documentId' is required and must be a non-empty string.");
        }

        if (!TryGetNonEmptyString(root, "content", out var content))
        {
            return MessageTranslation.PermanentFailure(DeadLetterReasons.MissingRequiredField,
                "'content' is required and must be a non-empty string.");
        }

        if (!string.IsNullOrEmpty(sessionId) && !string.Equals(sessionId, documentId, StringComparison.Ordinal))
        {
            return MessageTranslation.PermanentFailure(DeadLetterReasons.SessionDocumentMismatch,
                $"SessionId '{sessionId}' does not match documentId '{documentId}'. A session gives per-document FIFO, so the session id is the document id.");
        }

        if (!TryReadTags(root, out var tags))
        {
            return MessageTranslation.PermanentFailure(DeadLetterReasons.MissingRequiredField,
                "'metadata', when present, must be a JSON object whose every value is a string.");
        }

        return MessageTranslation.Success(BuildJob(documentId, content, tags));
    }

    /// <remarks>
    /// <see cref="IngestionJob.Options"/> is deliberately left unset. Re-ingest is an
    /// unconditional replace for BM25 and the data manager regardless of
    /// <c>IngestionOptions.Overwrite</c>, which is precisely what an at-least-once transport
    /// needs: a redelivered message re-ingests rather than duplicates, with no per-message
    /// configuration to get wrong.
    /// </remarks>
    private static IngestionJob BuildJob(string documentId, string content, Dictionary<string, MetadataValue> tags) =>
        new()
        {
            Content = Encoding.UTF8.GetBytes(content),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId(documentId),
                FileName = $"{FileNameSanitizer.Sanitize(documentId, FallbackStem)}.txt",
                Tags = tags,
            },
        };

    private static bool TryReadTags(JsonElement root, out Dictionary<string, MetadataValue> tags)
    {
        tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        if (!root.TryGetProperty("metadata", out var metadata))
            return true;

        if (metadata.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                return false;
            tags[property.Name] = property.Value.GetString()!;
        }

        return true;
    }

    private static bool TryGetNonEmptyString(JsonElement element, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        value = text;
        return true;
    }
}
