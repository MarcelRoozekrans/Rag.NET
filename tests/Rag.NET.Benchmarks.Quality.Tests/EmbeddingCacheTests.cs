using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="EmbeddingCache"/> against a fake embedder and a temporary directory. <b>No model
/// file, no network.</b>
/// <para>
/// Two of these tests are the reason the class exists rather than a dictionary. A cache keyed on
/// text alone hands back the previous model's vectors after a model change, and a cache that
/// believes a half-written file hands back a truncated one after an interrupted run. Neither throws;
/// both make every benchmark number downstream quietly wrong while the suite stays green. The hit
/// and miss tests below are table stakes next to those.
/// </para>
/// </summary>
public sealed class EmbeddingCacheTests : IDisposable
{
    private const string Text = "Cerebral white matter alterations of the architecture.";

    private readonly string _root;

    public EmbeddingCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ragnet-embed-cache-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Miss_EmbedsAndStores()
    {
        var embedder = new RecordingEmbedder(1f);
        var cache = new EmbeddingCache(_root, "all-MiniLM-L6-v2@r1");

        var vectors = await cache.GetOrAddAsync([Text], embedder.EmbedAsync, Ct);

        Assert.Equal(1, embedder.Calls);
        Assert.Equal(new[] { Text }, embedder.Texts);
        Assert.Equal(new[] { 1f, Text.Length, 0f }, vectors[0]);
        Assert.Equal(0L, cache.Hits);
        Assert.Equal(1L, cache.Misses);
        _ = Assert.Single(EntryFiles());
    }

    [Fact]
    public async Task Hit_ReturnsTheStoredVector_WithoutEmbedding()
    {
        var first = new RecordingEmbedder(1f);
        var written = await new EmbeddingCache(_root, "all-MiniLM-L6-v2@r1")
            .GetOrAddAsync([Text], first.EmbedAsync, Ct);

        // A second cache over the same directory, so the hit comes off disk rather than out of any
        // in-process state the first one might have kept.
        var second = new RecordingEmbedder(99f);
        var cache = new EmbeddingCache(_root, "all-MiniLM-L6-v2@r1");

        var vectors = await cache.GetOrAddAsync([Text], second.EmbedAsync, Ct);

        Assert.Equal(0, second.Calls);
        Assert.Equal(written[0], vectors[0]);
        Assert.Equal(1L, cache.Hits);
        Assert.Equal(0L, cache.Misses);
    }

    [Fact]
    public async Task SameTextUnderADifferentModelId_IsAMiss()
    {
        // The test this class exists for. Keyed on text alone, the second call returns the first
        // model's vectors: nothing throws, nothing is skipped, and every number measured afterwards
        // belongs to a model that was not used.
        var old = new RecordingEmbedder(1f);
        _ = await new EmbeddingCache(_root, "all-MiniLM-L6-v2@r1").GetOrAddAsync([Text], old.EmbedAsync, Ct);

        var current = new RecordingEmbedder(2f);
        var cache = new EmbeddingCache(_root, "all-MiniLM-L6-v2@r2");

        var vectors = await cache.GetOrAddAsync([Text], current.EmbedAsync, Ct);

        Assert.Equal(1, current.Calls);
        Assert.Equal(1L, cache.Misses);
        Assert.Equal(0L, cache.Hits);
        Assert.Equal(2f, vectors[0][0]);
        Assert.Equal(2, EntryFiles().Count);
    }

    [Fact]
    public async Task ADifferentModelIdWithTheSameTextGetsItsOwnEntry_NotTheOtherModelsFile()
    {
        // The stronger form of the test above: after both models have embedded the same text, each
        // still reads back its own vector. One entry overwriting the other would pass the miss
        // assertions and still be wrong.
        _ = await new EmbeddingCache(_root, "model-a").GetOrAddAsync([Text], new RecordingEmbedder(1f).EmbedAsync, Ct);
        _ = await new EmbeddingCache(_root, "model-b").GetOrAddAsync([Text], new RecordingEmbedder(2f).EmbedAsync, Ct);

        var a = await new EmbeddingCache(_root, "model-a").GetOrAddAsync([Text], Explode, Ct);
        var b = await new EmbeddingCache(_root, "model-b").GetOrAddAsync([Text], Explode, Ct);

        Assert.Equal(1f, a[0][0]);
        Assert.Equal(2f, b[0][0]);
    }

    [Fact]
    public async Task ATruncatedEntry_IsAMiss_NotAShortVector()
    {
        // What an interrupted run leaves behind. Returning the surviving prefix would be a vector of
        // the wrong dimension, which fails loudly; returning it as if whole is the dangerous case,
        // and either way the answer is to embed it again.
        _ = await new EmbeddingCache(_root, "model-a").GetOrAddAsync([Text], new RecordingEmbedder(1f).EmbedAsync, Ct);

        var entry = Assert.Single(EntryFiles());
        var bytes = await File.ReadAllBytesAsync(entry, Ct);
        await File.WriteAllBytesAsync(entry, bytes[..^4], Ct);

        var embedder = new RecordingEmbedder(7f);
        var cache = new EmbeddingCache(_root, "model-a");
        var vectors = await cache.GetOrAddAsync([Text], embedder.EmbedAsync, Ct);

        Assert.Equal(1, embedder.Calls);
        Assert.Equal(1L, cache.Misses);
        Assert.Equal(7f, vectors[0][0]);
    }

    [Fact]
    public async Task ACorruptEntry_IsAMiss()
    {
        _ = await new EmbeddingCache(_root, "model-a").GetOrAddAsync([Text], new RecordingEmbedder(1f).EmbedAsync, Ct);

        var entry = Assert.Single(EntryFiles());
        await File.WriteAllBytesAsync(entry, [0xDE, 0xAD, 0xBE, 0xEF], Ct);

        var embedder = new RecordingEmbedder(7f);
        var cache = new EmbeddingCache(_root, "model-a");
        var vectors = await cache.GetOrAddAsync([Text], embedder.EmbedAsync, Ct);

        Assert.Equal(1, embedder.Calls);
        Assert.Equal(7f, vectors[0][0]);
    }

    [Fact]
    public async Task AnEntryWhoseBodyWasRewritten_IsAMiss_BecauseTheDigestNoLongerAgrees()
    {
        // Intact length, correct magic, wrong contents — the case a length check alone cannot see.
        _ = await new EmbeddingCache(_root, "model-a").GetOrAddAsync([Text], new RecordingEmbedder(1f).EmbedAsync, Ct);

        var entry = Assert.Single(EntryFiles());
        var bytes = await File.ReadAllBytesAsync(entry, Ct);
        bytes[10] ^= 0xFF;
        await File.WriteAllBytesAsync(entry, bytes, Ct);

        var embedder = new RecordingEmbedder(7f);
        var vectors = await new EmbeddingCache(_root, "model-a").GetOrAddAsync([Text], embedder.EmbedAsync, Ct);

        Assert.Equal(1, embedder.Calls);
        Assert.Equal(7f, vectors[0][0]);
    }

    [Fact]
    public async Task ATextRepeatedInOneBatch_IsEmbeddedOnce_AndFillsEverySlot()
    {
        var embedder = new RecordingEmbedder(1f);
        var cache = new EmbeddingCache(_root, "model-a");

        var vectors = await cache.GetOrAddAsync([Text, "other", Text], embedder.EmbedAsync, Ct);

        Assert.Equal(new[] { Text, "other" }, embedder.Texts);
        Assert.Equal(vectors[0], vectors[2]);
        Assert.NotEqual(vectors[0], vectors[1]);
    }

    [Fact]
    public async Task MixedHitsAndMisses_KeepEveryVectorAgainstItsOwnText()
    {
        var first = new RecordingEmbedder(1f);
        var seeded = await new EmbeddingCache(_root, "model-a").GetOrAddAsync(["b"], first.EmbedAsync, Ct);

        var second = new RecordingEmbedder(5f);
        var cache = new EmbeddingCache(_root, "model-a");
        var vectors = await cache.GetOrAddAsync(["a", "b", "c"], second.EmbedAsync, Ct);

        // Only the two misses reach the embedder, and the cached one lands in the middle slot rather
        // than shifting the other two along by one.
        Assert.Equal(new[] { "a", "c" }, second.Texts);
        Assert.Equal(seeded[0], vectors[1]);
        Assert.Equal(5f, vectors[0][0]);
        Assert.Equal(5f, vectors[2][0]);
        Assert.Equal(1L, cache.Hits);
        Assert.Equal(2L, cache.Misses);
    }

    [Fact]
    public async Task AnEmbedderReturningTheWrongNumberOfVectors_Throws_RatherThanStoringThemOneOut()
    {
        var cache = new EmbeddingCache(_root, "model-a");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrAddAsync(
                ["a", "b"],
                (_, _) => Task.FromResult<IReadOnlyList<float[]>>([[1f]]),
                Ct));

        Assert.Contains("2 texts", exception.Message, StringComparison.Ordinal);
        Assert.Empty(EntryFiles());
    }

    [Fact]
    public async Task Prefetch_ServesEveryLaterLookupFromMemory_EvenAfterTheFilesAreGone()
    {
        var seeded = await new EmbeddingCache(_root, "model-a")
            .GetOrAddAsync([Text, "other"], new RecordingEmbedder(1f).EmbedAsync, Ct);

        var cache = new EmbeddingCache(_root, "model-a");
        cache.Prefetch([Text, "other"]);

        // Deleting the entries proves the timed-path lookups cannot be touching disk: a cache
        // that still read files here would miss and call the exploding embedder.
        foreach (var file in EntryFiles())
        {
            File.Delete(file);
        }

        var vectors = await cache.GetOrAddAsync([Text, "other"], Explode, Ct);

        Assert.Equal(seeded[0], vectors[0]);
        Assert.Equal(seeded[1], vectors[1]);

        // The counters moved at prefetch time — once per occurrence, what the disk really held —
        // and the in-memory serving did not move them again, so a sidecar's counts stay the
        // counts a pre-prefetch run would have reported.
        Assert.Equal(2L, cache.Hits);
        Assert.Equal(0L, cache.Misses);
    }

    [Fact]
    public async Task Prefetch_CountsARepeatedText_OncePerOccurrence()
    {
        _ = await new EmbeddingCache(_root, "model-a")
            .GetOrAddAsync([Text], new RecordingEmbedder(1f).EmbedAsync, Ct);

        var cache = new EmbeddingCache(_root, "model-a");
        cache.Prefetch([Text, Text]);

        Assert.Equal(2L, cache.Hits);
        Assert.Equal(0L, cache.Misses);
    }

    [Fact]
    public async Task Prefetch_WithTextsNotCached_Throws_NamingTheCount_RatherThanEmbedding()
    {
        // A cold cache must fail loudly: silently embedding during a prefetch would pay model
        // and disk costs no other run paid, one level above the spans the prefetch protects.
        _ = await new EmbeddingCache(_root, "model-a")
            .GetOrAddAsync([Text], new RecordingEmbedder(1f).EmbedAsync, Ct);

        var cache = new EmbeddingCache(_root, "model-a");
        var exception = Assert.Throws<InvalidOperationException>(
            () => cache.Prefetch([Text, "never embedded", "also never embedded"]));

        Assert.Contains("2 of the 3", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1L, cache.Hits);
        Assert.Equal(2L, cache.Misses);
    }

    [Fact]
    public async Task GetOrAddAsync_AfterPrefetch_RefusesATextThePrefetchNeverSaw()
    {
        _ = await new EmbeddingCache(_root, "model-a")
            .GetOrAddAsync([Text, "other"], new RecordingEmbedder(1f).EmbedAsync, Ct);

        var cache = new EmbeddingCache(_root, "model-a");
        cache.Prefetch([Text]);

        // "other" IS on disk — refusing anyway is the point. After a prefetch, a lookup that
        // fell back to disk would put file I/O back inside the timed span the prefetch exists
        // to keep it out of.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrAddAsync(["other"], Explode, Ct));

        Assert.Contains("1 of the 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsABlankModelIdentity()
    {
        // A blank identity is the same failure as no identity at all: every model shares one key
        // space, and the first model's vectors are handed to the second.
        _ = Assert.Throws<ArgumentException>(() => new EmbeddingCache(_root, "  "));
    }

    [Fact]
    public void EntriesLiveUnderTheCacheRoot_NeverBesideTheCode()
    {
        var cache = new EmbeddingCache(_root, "model-a");

        Assert.Equal(
            Path.Combine(_root, EmbeddingCache.DirectoryName),
            cache.EntryDirectory,
            StringComparer.Ordinal);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An embedder that must never be called, so a silent miss fails loudly.</summary>
    private static readonly Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> Explode =
        (_, _) => throw new InvalidOperationException(
            "The cache embedded a text it should have read from disk.");

    private IReadOnlyList<string> EntryFiles()
    {
        var directory = Path.Combine(_root, EmbeddingCache.DirectoryName);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.vec", SearchOption.AllDirectories)
            : [];
    }

    /// <summary>
    /// A fake embedder that records what it was asked for and returns a vector carrying its own
    /// seed, so a vector from the wrong embedder is visible rather than merely plausible.
    /// </summary>
    private sealed class RecordingEmbedder
    {
        private readonly float _seed;
        private readonly List<string> _texts = [];

        public RecordingEmbedder(float seed)
        {
            _seed = seed;
        }

        public int Calls { get; private set; }

        public IReadOnlyList<string> Texts => _texts;

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            _texts.AddRange(texts);

            var vectors = new float[texts.Count][];
            for (var i = 0; i < texts.Count; i++)
            {
                vectors[i] = [_seed, texts[i].Length, i];
            }

            return Task.FromResult<IReadOnlyList<float[]>>(vectors);
        }
    }
}
