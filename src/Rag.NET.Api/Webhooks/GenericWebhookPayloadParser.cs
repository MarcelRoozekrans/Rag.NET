using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Rag.NET.Models;

namespace Rag.NET.Api.Webhooks;

/// <summary>
/// Default payload parser: accepts a single <c>{ "documentId": "...", "content": "...",
/// "metadata": { ... } }</c> object or an array of them. <c>content</c> is required and
/// non-empty; <c>metadata</c> (optional) must be a flat string dictionary and maps to
/// <see cref="DocumentMetadata.Tags"/>; the file name defaults to <c>{documentId}.txt</c>.
/// </summary>
public sealed class GenericWebhookPayloadParser : IWebhookPayloadParser
{
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

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
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
                FileName = $"{documentId}.txt",
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
