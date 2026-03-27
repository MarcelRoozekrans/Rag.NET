using BenchmarkDotNet.Attributes;
using Rag.NET.Models;
using Rag.NET.Search;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Measures the cost of BM25 indexing and search with synonym expansion.
/// Key concern: the multi-word phrase scan in <c>Tokenize</c> is O(n²) in base token count.
/// These benchmarks expose whether that cost is acceptable at realistic chunk sizes
/// and synonym map sizes.
/// </summary>
[MemoryDiagnoser]
public class Bm25SynonymBenchmarks
{
    private string _shortText = null!;    // ~40 base tokens
    private string _mediumText = null!;   // ~200 base tokens
    private string _longText = null!;     // ~800 base tokens

    private SynonymMap _smallMap = null!;  // 10 single-word synonym groups
    private SynonymMap _largeMap = null!;  // 100 single-word synonym groups
    private SynonymMap _phraseMap = null!; // 10 multi-word synonym groups

    private InMemoryBm25Index _indexNoSynonyms = null!;
    private InMemoryBm25Index _indexSmallMap = null!;
    private InMemoryBm25Index _indexLargeMap = null!;

    [GlobalSetup]
    public void Setup()
    {
        const string sentence = "The quick brown fox jumps over the lazy dog. ";
        _shortText  = Repeat(sentence, 5);
        _mediumText = Repeat(sentence, 25);
        _longText   = Repeat(sentence, 100);

        _smallMap  = BuildSingleWordMap(10);
        _largeMap  = BuildSingleWordMap(100);
        _phraseMap = BuildPhraseMap(10);

        _indexNoSynonyms = BuildIndex(null,       50);
        _indexSmallMap   = BuildIndex(_smallMap,  50);
        _indexLargeMap   = BuildIndex(_largeMap,  50);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _smallMap.Dispose();
        _largeMap.Dispose();
        _phraseMap.Dispose();
        _indexNoSynonyms.Dispose();
        _indexSmallMap.Dispose();
        _indexLargeMap.Dispose();
    }

    // ── Add (index time) ──────────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    public void Add_Short_NoSynonyms() => AddOneChunk(_shortText, null);

    [Benchmark]
    public void Add_Short_SmallMap() => AddOneChunk(_shortText, _smallMap);

    [Benchmark]
    public void Add_Short_LargeMap() => AddOneChunk(_shortText, _largeMap);

    [Benchmark]
    public void Add_Medium_NoSynonyms() => AddOneChunk(_mediumText, null);

    [Benchmark]
    public void Add_Medium_SmallMap() => AddOneChunk(_mediumText, _smallMap);

    [Benchmark]
    public void Add_Medium_LargeMap() => AddOneChunk(_mediumText, _largeMap);

    [Benchmark]
    public void Add_Long_NoSynonyms() => AddOneChunk(_longText, null);

    [Benchmark]
    public void Add_Long_SmallMap() => AddOneChunk(_longText, _smallMap);

    [Benchmark]
    public void Add_Long_LargeMap() => AddOneChunk(_longText, _largeMap);

    [Benchmark]
    public void Add_Medium_PhraseMap() => AddOneChunk(_mediumText, _phraseMap);

    // ── Search (query time) ───────────────────────────────────────────────────

    [Benchmark]
    public IReadOnlyList<(TextChunk, double)> Search_NoSynonyms()
        => _indexNoSynonyms.Search("fox", topK: 10);

    [Benchmark]
    public IReadOnlyList<(TextChunk, double)> Search_SmallMap()
        => _indexSmallMap.Search("fox", topK: 10);

    [Benchmark]
    public IReadOnlyList<(TextChunk, double)> Search_LargeMap()
        => _indexLargeMap.Search("fox", topK: 10);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddOneChunk(string text, SynonymMap? map)
    {
        using var index = new InMemoryBm25Index(map);
        index.Add(0, new TextChunk
        {
            Text = text,
            DocumentId = new DocumentId("bench"),
            ChunkIndex = 0,
        });
    }

    private static string Repeat(string text, int times)
    {
        var sb = new System.Text.StringBuilder(text.Length * times);
        for (int i = 0; i < times; i++) sb.Append(text);
        return sb.ToString();
    }

    private static SynonymMap BuildSingleWordMap(int groupCount)
    {
        var map = new SynonymMap();
        for (int i = 0; i < groupCount; i++)
            map.AddGroup($"terma{i}", $"termb{i}");
        return map;
    }

    private static SynonymMap BuildPhraseMap(int groupCount)
    {
        var map = new SynonymMap();
        for (int i = 0; i < groupCount; i++)
            map.AddGroup($"phrase one {i}", $"phrase two {i}");
        return map;
    }

    private static InMemoryBm25Index BuildIndex(SynonymMap? map, int docCount)
    {
        const string sentence = "The quick brown fox jumps over the lazy dog. ";
        var index = new InMemoryBm25Index(map);
        for (int i = 0; i < docCount; i++)
            index.Add(i, new TextChunk
            {
                Text = sentence + $"doc {i}",
                DocumentId = new DocumentId($"doc-{i}"),
                ChunkIndex = 0,
            });
        return index;
    }
}
