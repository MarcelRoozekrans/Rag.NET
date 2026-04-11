using System.Text.Json.Serialization;

namespace Rag.NET;

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class RagJsonSerializerContext : JsonSerializerContext;
