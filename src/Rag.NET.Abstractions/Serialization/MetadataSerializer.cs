using System.Text.Json;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET;

/// <summary>
/// The shared chunk-metadata JSON round-trip used by every store that persists metadata as one
/// document (Qdrant's <c>metadata</c> payload field, Weaviate's <c>metadata_json</c> property,
/// PgVector's <c>jsonb</c> column, the SQLite stores, and Azure AI Search's legacy
/// <c>metadata</c> field). Values keep their <see cref="MetadataValue.Kind"/> through the trip —
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

    public static string SerializeMetadata(IDictionary<string, MetadataValue> metadata)
    {
        var dict = metadata as Dictionary<string, MetadataValue>
            ?? new Dictionary<string, MetadataValue>(metadata, StringComparer.Ordinal);
        return JsonSerializer.Serialize(dict, RagJsonSerializerContext.Default.DictionaryStringMetadataValue);
    }

    /// <summary>
    /// Round-trip for plain string-to-string maps (<see cref="DocumentMetadata.Tags"/> in the
    /// SQLite sidecar store). Same wire shape those rows always had; chunk metadata does not
    /// come through here.
    /// </summary>
    public static Result<Dictionary<string, string>, RagError> DeserializeTags(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return Result<Dictionary<string, string>, RagError>.Success(new Dictionary<string, string>(StringComparer.Ordinal));

        try
        {
            var result = JsonSerializer.Deserialize(json,
                RagJsonSerializerContext.Default.DictionaryStringString)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            return Result<Dictionary<string, string>, RagError>.Success(result);
        }
        catch (JsonException ex)
        {
            return Result<Dictionary<string, string>, RagError>.Failure(new RagError.StorageFailed(ex));
        }
    }

    /// <inheritdoc cref="DeserializeTags"/>
    public static string SerializeTags(IDictionary<string, string> tags)
    {
        var dict = tags as Dictionary<string, string>
            ?? new Dictionary<string, string>(tags, StringComparer.Ordinal);
        return JsonSerializer.Serialize(dict, RagJsonSerializerContext.Default.DictionaryStringString);
    }
}
