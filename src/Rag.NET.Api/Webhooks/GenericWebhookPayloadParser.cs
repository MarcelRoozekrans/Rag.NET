using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Rag.NET.Models;

namespace Rag.NET.Api.Webhooks;

/// <summary>
/// Default payload parser: accepts a single <c>{ "documentId": "...", "content": "...",
/// "metadata": { ... } }</c> object or an array of them. <c>content</c> is required and
/// non-empty; <c>metadata</c> (optional) must be a flat string dictionary and maps to
/// <see cref="DocumentMetadata.Tags"/>; the file name defaults to <c>{documentId}.txt</c> with
/// the stem passed through <see cref="FileNameSanitizer"/>.
/// </summary>
public sealed class GenericWebhookPayloadParser : IWebhookPayloadParser
{
    /// <summary>
    /// Stem used when <c>documentId</c> sanitizes away to nothing (<c>"..."</c>, <c>"///"</c>).
    /// </summary>
    /// <remarks>
    /// Such an id is not rejected. <c>documentId</c> is already validated as present and
    /// non-whitespace, and it is carried through verbatim as the <see cref="DocumentId"/> — the
    /// document's identity. <see cref="DocumentMetadata.FileName"/> is display and
    /// parser-selection metadata, never a path, so collapsing an unrenderable id to a shared
    /// stem loses nothing that identity does not already carry, and turning a currently-accepted
    /// payload into a 400 would be a behaviour change unrelated to the traversal fix.
    /// </remarks>
    private const string FallbackStem = "document";

    /// <inheritdoc/>
    public bool TryParse(JsonElement payload, [NotNullWhen(true)] out IReadOnlyList<IngestionJob>? jobs)
    {
        jobs = null;
        var parsed = new List<IngestionJob>();

        if (payload.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in payload.EnumerateArray())
            {
                if (!TryParseSingle(element, out var job))
                    return false;
                parsed.Add(job);
            }

            if (parsed.Count == 0)
                return false; // an empty array carries no documents — reject as malformed
        }
        else
        {
            if (!TryParseSingle(payload, out var job))
                return false;
            parsed.Add(job);
        }

#pragma warning disable HLQ001 // IReadOnlyList<T> is the contract; boxing the enumerator is a per-request, non-hot-path cost
        jobs = parsed;
#pragma warning restore HLQ001
        return true;
    }

    private static bool TryParseSingle(JsonElement element, [NotNullWhen(true)] out IngestionJob? job)
    {
        job = null;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryGetNonEmptyString(element, "documentId", out var documentId))
            return false;
        if (!TryGetNonEmptyString(element, "content", out var content))
            return false;

        var tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        if (element.TryGetProperty("metadata", out var metadata))
        {
            if (metadata.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    return false;
                tags[property.Name] = property.Value.GetString()!;
            }
        }

        job = new IngestionJob
        {
            Content = Encoding.UTF8.GetBytes(content),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId(documentId),
                // Untrusted: a documentId of "../../etc/passwd" must not become a file name
                // carrying path separators or traversal segments.
                FileName = $"{FileNameSanitizer.Sanitize(documentId, FallbackStem)}.txt",
                Tags = tags,
            },
        };
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
