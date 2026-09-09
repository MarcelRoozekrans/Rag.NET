using System.Text.Json;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET;

/// <summary>
/// The shared chunk-metadata JSON round-trip used by every store that persists metadata as one
/// document (Qdrant's <c>metadata</c> payload field, Weaviate's <c>metadata_json</c> property,
/// PgVector's <c>jsonb</c> column, the SQLite stores, Redis's <c>metadata</c> hash field, and
/// Azure AI Search's legacy <c>metadata</c> field). Values keep their
/// <see cref="MetadataValue.Kind"/> through the trip —
/// see <see cref="MetadataValueJsonConverter"/> for the format, including why metadata written
/// before values carried types (JSON with string values only) reads back losslessly as
/// <see cref="MetadataValueKind.String"/> values.
/// </summary>
internal static class MetadataSerializer
{
    public static Result<Dictionary<string, MetadataValue>, RagError> DeserializeMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return Result<Dictionary<string, MetadataValue>, RagError>.Success(new Dictionary<string, MetadataValue>(StringComparer.Ordinal));

        try
        {
            var result = JsonSerializer.Deserialize(json,
                RagJsonSerializerContext.Default.DictionaryStringMetadataValue)
                ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
            return Result<Dictionary<string, MetadataValue>, RagError>.Success(result);
        }
        catch (JsonException ex)
        {
            return Result<Dictionary<string, MetadataValue>, RagError>.Failure(new RagError.StorageFailed(ex));
        }
    }

    /// <summary>
    /// <see cref="DeserializeMetadata"/>, except that corrupt JSON is treated as backend
    /// corruption rather than an absence to paper over. Exists because eight call sites across
    /// five components read a persisted metadata blob, and before #521 only two of them
    /// (Weaviate, reviewed in <c>98b327fd</c>; Redis, which followed that precedent) actually
    /// surfaced a parse failure — the other six inherited a tolerant default from a mechanical
    /// serializer migration (<c>179e4f8e</c>) that nobody had revisited. A chunk that reads as
    /// having no metadata is indistinguishable from one that genuinely has none, so a metadata
    /// filter over a corrupt value quietly does not match. Routing every site through this helper
    /// means the tolerant shape is no longer the easy one to write — a new call site has to
    /// deliberately avoid this method to reintroduce the swallow.
    /// <c>DeserializeMetadata(null)</c> and <c>DeserializeMetadata("")</c> both succeed with an
    /// empty dictionary, so this cannot fire on a field that was never written — only on stored
    /// JSON that a <see cref="JsonException"/> could not parse.
    /// </summary>
    /// <param name="json">The raw stored value. <see langword="null"/> or empty reads as no
    /// metadata, exactly like <see cref="DeserializeMetadata"/>.</param>
    /// <param name="context">
    /// Identifies the row being read, for the exception message: the store's name, the document
    /// (and chunk index, where the row is chunk-scoped), and the field or column holding the
    /// JSON. A message that only says "metadata was corrupt" is not worth the throw.
    /// </param>
    /// <returns>The deserialized metadata.</returns>
    /// <exception cref="InvalidOperationException">
    /// The stored JSON could not be parsed. Carries the original <see cref="JsonException"/> as
    /// <see cref="Exception.InnerException"/> — a throw that discarded it would make the
    /// corruption harder to diagnose than the silence it replaces.
    /// </exception>
    internal static Dictionary<string, MetadataValue> DeserializeMetadataOrThrow(string? json, string context)
    {
        var result = DeserializeMetadata(json);
        if (result.IsSuccess)
            return result.Value;

        var inner = result.Error is RagError.StorageFailed(var ex) ? ex : null;
        throw new InvalidOperationException($"{context}: the stored metadata JSON could not be parsed.", inner);
    }

    public static string SerializeMetadata(IDictionary<string, MetadataValue> metadata)
    {
        var dict = metadata as Dictionary<string, MetadataValue>
            ?? new Dictionary<string, MetadataValue>(metadata, StringComparer.Ordinal);
        return JsonSerializer.Serialize(dict, RagJsonSerializerContext.Default.DictionaryStringMetadataValue);
    }

    /// <summary>
    /// Round-trip for <see cref="DocumentMetadata.Tags"/> snapshots in the SQLite sidecar store.
    /// Same typed wire shape as chunk metadata; rows written before tags carried types (JSON with
    /// string values only) read back losslessly as <see cref="MetadataValueKind.String"/> values.
    /// </summary>
    public static Result<Dictionary<string, MetadataValue>, RagError> DeserializeTags(string? json) =>
        DeserializeMetadata(json);

    /// <inheritdoc cref="DeserializeTags"/>
    public static string SerializeTags(IDictionary<string, MetadataValue> tags) =>
        SerializeMetadata(tags);
}
