using System.Text.Json.Serialization;
using Rag.NET.Models;

namespace Rag.NET;

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, MetadataValue>))]
[JsonSerializable(typeof(List<string>))]
internal partial class RagJsonSerializerContext : JsonSerializerContext;
