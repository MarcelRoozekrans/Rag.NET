using BenchmarkDotNet.Attributes;
using Rag.NET.Models;
using Rag.NET.Security;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU-only overhead of <see cref="RbacRetrievalGuard.Inspect"/>.
/// No I/O or allocations from the pipeline — measures pure role-intersection logic.
/// </summary>
[MemoryDiagnoser]
public class RbacRetrievalGuardBenchmarks
{
    private IReadOnlyList<SearchResult> _allPublic = null!;
    private IReadOnlyList<SearchResult> _allRestricted = null!;
    private IReadOnlyList<SearchResult> _mixed = null!;
    private IReadOnlyList<SearchResult> _manyRoles = null!;

    private RbacRetrievalGuard _guardHr = null!;
    private RbacRetrievalGuard _guardUser = null!;
    private RbacRetrievalGuard _guardManyRoles = null!;

    [GlobalSetup]
    public void Setup()
    {
        _allPublic      = BuildResults(100, allowedRoles: null);
        _allRestricted  = BuildResults(100, allowedRoles: "hr");
        _mixed          = [..BuildResults(50, allowedRoles: null), ..BuildResults(50, allowedRoles: "hr")];
        _manyRoles      = BuildResults(100, allowedRoles: "hr,finance,legal");

        _guardHr        = new RbacRetrievalGuard(new FakeCallerContext(["hr"]));
        _guardUser      = new RbacRetrievalGuard(new FakeCallerContext(["user"]));
        _guardManyRoles = new RbacRetrievalGuard(new FakeCallerContext(
            ["engineering", "devops", "qa", "support", "ops", "product", "design", "data", "finance", "marketing"]));
    }

    /// <summary>100 public chunks — guard takes the fast pass-through path (no allocation).</summary>
    [Benchmark(Baseline = true)]
    public int Inspect_AllPublic_NoFiltering()
        => _guardUser.Inspect(_allPublic).Count;

    /// <summary>100 chunks restricted to "hr", caller has "hr" — all pass through.</summary>
    [Benchmark]
    public int Inspect_AllRestricted_CallerHasAccess()
        => _guardHr.Inspect(_allRestricted).Count;

    /// <summary>50 public + 50 "hr"-restricted chunks, caller has "user" — half filtered.</summary>
    [Benchmark]
    public int Inspect_MixedChunks_HalfFiltered()
        => _guardUser.Inspect(_mixed).Count;

    /// <summary>100 chunks with 3 allowed roles, caller has 10 roles including "finance" — intersection check.</summary>
    [Benchmark]
    public int Inspect_ManyRoles_IntersectionCheck()
        => _guardManyRoles.Inspect(_manyRoles).Count;

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<SearchResult> BuildResults(int count, string? allowedRoles)
    {
        var results = new SearchResult[count];
        for (var i = 0; i < count; i++)
        {
            var chunk = new TextChunk
            {
                Text        = $"Chunk text {i}.",
                DocumentId  = new DocumentId($"doc-{i}"),
                ChunkIndex  = i,
            };
            if (allowedRoles is not null)
                chunk.Metadata["allowed_roles"] = allowedRoles;
            results[i] = new SearchResult { Score = 0.9, Chunk = chunk };
        }
        return results;
    }

    private sealed class FakeCallerContext(IReadOnlyList<string> roles) : ICallerContext
    {
        public IReadOnlyList<string> GetRoles() => roles;
    }
}

/// <summary>
/// Benchmarks the per-chunk regex execution cost of <see cref="PiiChunkSanitiser.Sanitise"/>.
/// Construction (regex compilation) is a one-time cost handled in <see cref="Setup"/>.
/// </summary>
[MemoryDiagnoser]
public class PiiChunkSanitiserBenchmarks
{
    private PiiChunkSanitiser _sanitiser = null!;
    private Dictionary<string, MetadataValue> _emptyMetadata = null!;

    private string _smallNoPii    = null!;
    private string _mediumNoPii   = null!;
    private string _largeNoPii    = null!;
    private string _smallWithPii  = null!;
    private string _largeMultiPii = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sanitiser     = new PiiChunkSanitiser(new PiiDetectionOptions());
        _emptyMetadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);

        const string sentence = "The quick brown fox jumps over the lazy dog and contributes to the benchmark suite. ";

        _smallNoPii   = Repeat(sentence, targetLength: 100);
        _mediumNoPii  = Repeat(sentence, targetLength: 1_000);
        _largeNoPii   = Repeat(sentence, targetLength: 10_000);

        // ~100 chars with an email embedded near the middle
        _smallWithPii = "Contact our support team at support@example.com for assistance with your request today.";

        // 10 K text with email, phone, and IP address scattered at 1/4, 1/2, 3/4 positions
        var quarter = Repeat(sentence, targetLength: 2_500);
        _largeMultiPii =
            quarter + " reach us at admin@acme.org or call " +
            quarter + " (555) 867-5309 for details; server at " +
            quarter + " 192.168.1.42 handles requests" +
            quarter;
    }

    /// <summary>100-char text with no PII — measures baseline regex scan cost.</summary>
    [Benchmark(Baseline = true)]
    public string Sanitise_SmallText_NoPii()
        => _sanitiser.Sanitise(_smallNoPii, _emptyMetadata);

    /// <summary>1 K-char text with no PII.</summary>
    [Benchmark]
    public string Sanitise_MediumText_NoPii()
        => _sanitiser.Sanitise(_mediumNoPii, _emptyMetadata);

    /// <summary>10 K-char text with no PII — measures cost growth with text length.</summary>
    [Benchmark]
    public string Sanitise_LargeText_NoPii()
        => _sanitiser.Sanitise(_largeNoPii, _emptyMetadata);

    /// <summary>~100-char text with one email address — measures a single redaction path.</summary>
    [Benchmark]
    public string Sanitise_SmallText_WithPii()
        => _sanitiser.Sanitise(_smallWithPii, _emptyMetadata);

    /// <summary>10 K-char text with email, phone, and IP address — measures multiple redactions.</summary>
    [Benchmark]
    public string Sanitise_LargeText_MultiplePii()
        => _sanitiser.Sanitise(_largeMultiPii, _emptyMetadata);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Repeat(string unit, int targetLength)
    {
        if (unit.Length >= targetLength)
            return unit[..targetLength];

        var sb = new System.Text.StringBuilder(targetLength + unit.Length);
        while (sb.Length < targetLength)
            sb.Append(unit);
        return sb.ToString(0, targetLength);
    }
}
