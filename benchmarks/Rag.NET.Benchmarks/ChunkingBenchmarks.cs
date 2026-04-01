using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Chunking.CSharp;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Benchmarks;

[MemoryDiagnoser]
public class ChunkingBenchmarks
{
    private DocumentSection _smallSection = null!;
    private DocumentSection _mediumSection = null!;
    private DocumentSection _largeSection = null!;
    private readonly RecursiveChunkingStrategy _recursive = new();
    private readonly FixedSizeChunkingStrategy _fixed = new();
    private readonly TokenAwareChunkingStrategy _tokenAware = new();
    private readonly CSharpChunkingStrategy _csharp = new(new CSharpChunkingOptions(), NullLogger<CSharpChunkingStrategy>.Instance);
    private readonly ChunkingOptions _options = new() { MaxChunkSize = 512, Overlap = 50 };
    private DocumentSection _smallCsharpSection = null!;
    private DocumentSection _largeCsharpSection = null!;

    [GlobalSetup]
    public void Setup()
    {
        _smallSection = CreateSection(GenerateText(500));
        _mediumSection = CreateSection(GenerateText(5_000));
        _largeSection = CreateSection(GenerateText(50_000));
        _smallCsharpSection = CreateSection(GenerateCsharpSource(10));   // 10 methods
        _largeCsharpSection = CreateSection(GenerateCsharpSource(100));  // 100 methods
    }

    [Benchmark]
    public async Task<int> Recursive_Small()
    {
        int count = 0;
        await foreach (var _ in _recursive.ChunkAsync(_smallSection, _options))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> Recursive_Medium()
    {
        int count = 0;
        await foreach (var _ in _recursive.ChunkAsync(_mediumSection, _options))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> Recursive_Large()
    {
        int count = 0;
        await foreach (var _ in _recursive.ChunkAsync(_largeSection, _options))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> Fixed_Small()
    {
        int count = 0;
        await foreach (var _ in _fixed.ChunkAsync(_smallSection, _options))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> Fixed_Medium()
    {
        int count = 0;
        await foreach (var _ in _fixed.ChunkAsync(_mediumSection, _options))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> Fixed_Large()
    {
        int count = 0;
        await foreach (var _ in _fixed.ChunkAsync(_largeSection, _options))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> TokenAware_Small()
    {
        int count = 0;
        await foreach (var _ in _tokenAware.ChunkAsync(_smallSection, _options))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> TokenAware_Medium()
    {
        int count = 0;
        await foreach (var _ in _tokenAware.ChunkAsync(_mediumSection, _options))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> TokenAware_Large()
    {
        int count = 0;
        await foreach (var _ in _tokenAware.ChunkAsync(_largeSection, _options))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> CSharp_Small()
    {
        int count = 0;
        await foreach (var _ in _csharp.ChunkAsync(_smallCsharpSection, _options))
            count++;
        return count;
    }

    [Benchmark]
    public async Task<int> CSharp_Large()
    {
        int count = 0;
        await foreach (var _ in _csharp.ChunkAsync(_largeCsharpSection, _options))
            count++;
        return count;
    }

    private static DocumentSection CreateSection(string text) => new()
    {
        Text = text,
        DocumentId = new DocumentId("bench-doc"),
        SectionIndex = 0,
    };

    private static string GenerateCsharpSource(int methodCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("namespace Bench;");
        sb.AppendLine("public class BenchClass {");
        for (int i = 0; i < methodCount; i++)
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"    public int Method{i}(int x) => x + {i};");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateText(int approximateLength)
    {
        const string paragraph = "The quick brown fox jumps over the lazy dog. " +
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit. " +
            "Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.\n\n";

        var sb = new System.Text.StringBuilder(approximateLength + paragraph.Length);
        while (sb.Length < approximateLength)
        {
            sb.Append(paragraph);
        }

        return sb.ToString();
    }
}
