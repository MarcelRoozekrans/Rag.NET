using System.Text.Json;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET;

internal static class MetadataSerializer
{
    public static Result<Dictionary<string, string>, RagError> DeserializeMetadata(string? json)
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

    public static string SerializeMetadata(IDictionary<string, string> metadata)
    {
        var dict = metadata as Dictionary<string, string>
            ?? new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        return JsonSerializer.Serialize(dict, RagJsonSerializerContext.Default.DictionaryStringString);
    }
}
