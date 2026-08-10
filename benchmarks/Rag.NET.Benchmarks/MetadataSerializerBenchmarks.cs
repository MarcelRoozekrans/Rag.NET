using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Rag.NET;
using Rag.NET.Models;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Compares reflection-based JSON serialization (pre-ZeroAlloc) against source-generated
/// serialization via <see cref="MetadataSerializer"/> and <see cref="RagJsonSerializerContext"/>.
/// Both paths produce identical output; the source-generated path eliminates runtime reflection
/// and is AOT/trimming-safe.
/// </summary>
[MemoryDiagnoser]
public class MetadataSerializerBenchmarks
{
    private Dictionary<string, MetadataValue> _metadata = null!;
    private string _json = null!;

    [GlobalSetup]
    public void Setup()
    {
        _metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["source"]      = "confluence",
            ["documentId"]  = "abc-123",
            ["title"]       = "Getting started with Rag.NET",
            ["author"]      = "jane.doe@example.com",
            ["createdAt"]   = "2026-01-15T08:30:00Z",
            ["page"]        = 3,
            ["reviewed"]    = true,
        };
        _json = MetadataSerializer.SerializeMetadata(_metadata);
    }

    // --- Serialize ---

    [Benchmark(Baseline = true)]
    public string Serialize_Reflection()
        => JsonSerializer.Serialize(_metadata);

    [Benchmark]
    public string Serialize_SourceGen()
        => MetadataSerializer.SerializeMetadata(_metadata);

    // --- Deserialize ---

    [Benchmark]
    public IDictionary<string, MetadataValue>? Deserialize_Reflection()
        => JsonSerializer.Deserialize<Dictionary<string, MetadataValue>>(_json);

    [Benchmark]
    public IDictionary<string, MetadataValue> Deserialize_SourceGen()
    {
        var result = MetadataSerializer.DeserializeMetadata(_json);
        return result.IsSuccess ? result.Value : new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
    }
}
