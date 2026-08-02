using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="HypotheticalCache"/> against a fake generator and a temporary directory. <b>No
/// LLM, no network.</b>
/// <para>
/// Two properties carry this class. First, the key covers all four of model, prompt template,
/// query and hypothesis index — the prompt-template test exists because editing the instruction and
/// silently reusing text generated from the old one is invisible everywhere downstream. Second,
/// refuse-on-miss throws rather than regenerates, because hosted LLMs are not bit-deterministic
/// even at temperature 0: a quiet regeneration would blend two generations into one table and the
/// suite would stay green while it happened.
/// </para>
/// </summary>
public sealed class HypotheticalCacheTests : IDisposable
{
    private const string Model = "gpt-4o-mini@2024-07-18|temp=0";
    private const string Prompt = "Write a short passage answering the question: {query}";
    private const string Query = "What is considered a business expense on a business trip?";
    private const string Hypothetical =
        "Deductible business expenses on a trip include transportation, lodging and half of meals.";

    private readonly string _root;

    public HypotheticalCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ragnet-hypo-cache-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Directory.Delete(recursive: true) with no retry is the recorded candidate cause of a
        // twice-seen, never-diagnosed flake in this test project: something external — an indexer,
        // a scanner — can briefly hold a just-written file, and then a cleanup failure fails a test
        // that ran perfectly. Two short retries outlast a transient handle; a delete that still
        // fails leaks one temporary directory instead of turning cleanup noise into a red build.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 2)
            {
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
            }

            Thread.Sleep(50);
        }
    }

    [Fact]
    public async Task Miss_GeneratesStoresAndReturnsTheText()
    {
        var generator = new RecordingGenerator(Hypothetical);
        var cache = Fill();

        var text = await cache.GetOrAddAsync(Query, 0, generator.GenerateAsync, Ct);

        Assert.Equal(Hypothetical, text);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(0L, cache.Hits);
        Assert.Equal(1L, cache.Misses);
        _ = Assert.Single(EntryFiles());
    }

    [Fact]
    public async Task Hit_ReturnsTheStoredText_WithoutGenerating()
    {
        _ = await Fill().GetOrAddAsync(Query, 0, new RecordingGenerator(Hypothetical).GenerateAsync, Ct);

        // A second cache over the same directory, so the hit comes off disk rather than out of any
        // in-process state the first one might have kept.
        var generator = new RecordingGenerator("a different generation entirely");
        var cache = Fill();

        var text = await cache.GetOrAddAsync(Query, 0, generator.GenerateAsync, Ct);

        Assert.Equal(Hypothetical, text);
        Assert.Equal(0, generator.Calls);
        Assert.Equal(1L, cache.Hits);
        Assert.Equal(0L, cache.Misses);
    }

    [Fact]
    public async Task RefuseOnMissMode_StillServesHits()
    {
        _ = await Fill().GetOrAddAsync(Query, 0, new RecordingGenerator(Hypothetical).GenerateAsync, Ct);

        var generator = new RecordingGenerator("must never be produced");
        var cache = Refuse();

        var text = await cache.GetOrAddAsync(Query, 0, generator.GenerateAsync, Ct);

        Assert.Equal(Hypothetical, text);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task TheSameQueryUnderADifferentModelId_IsAMiss()
    {
        _ = await Fill().GetOrAddAsync(Query, 0, new RecordingGenerator(Hypothetical).GenerateAsync, Ct);

        var generator = new RecordingGenerator("text from the new model");
        var cache = Fill(model: "gpt-4o@2024-08-06|temp=0");

        var text = await cache.GetOrAddAsync(Query, 0, generator.GenerateAsync, Ct);

        Assert.Equal("text from the new model", text);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(1L, cache.Misses);
        Assert.Equal(2, EntryFiles().Count);
    }

    [Fact]
    public async Task TheSameQueryAndModelUnderADifferentPromptTemplate_IsAMiss()
    {
        // The test this key shape exists for. An edited prompt is a different instruction, and a
        // cache that hands back text generated from the old instruction says nothing anywhere:
        // every number downstream simply belongs to a prompt that is no longer in the repository.
        _ = await Fill().GetOrAddAsync(Query, 0, new RecordingGenerator(Hypothetical).GenerateAsync, Ct);

        var generator = new RecordingGenerator("text from the edited prompt");
        var cache = Fill(prompt: "Write a detailed financial answer to: {query}");

        var text = await cache.GetOrAddAsync(Query, 0, generator.GenerateAsync, Ct);

        Assert.Equal("text from the edited prompt", text);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(1L, cache.Misses);
        Assert.Equal(2, EntryFiles().Count);
    }

    [Fact]
    public async Task ADifferentHypothesisIndex_IsAMiss()
    {
        _ = await Fill().GetOrAddAsync(Query, 0, new RecordingGenerator(Hypothetical).GenerateAsync, Ct);

        var generator = new RecordingGenerator("the second hypothesis");
        var cache = Fill();

        var text = await cache.GetOrAddAsync(Query, 1, generator.GenerateAsync, Ct);

        Assert.Equal("the second hypothesis", text);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(2, EntryFiles().Count);
    }

    [Fact]
    public async Task EveryKeyComponentGetsItsOwnEntry_NothingOverwrites()
    {
        // The stronger form of the miss tests above: after entries exist under two models, two
        // prompts and two indices, each combination still reads back its own text. One entry
        // overwriting another would pass every miss assertion and still be wrong.
        _ = await Fill().GetOrAddAsync(Query, 0, new RecordingGenerator("base").GenerateAsync, Ct);
        _ = await Fill().GetOrAddAsync(Query, 1, new RecordingGenerator("index 1").GenerateAsync, Ct);
        _ = await Fill(model: "other-model").GetOrAddAsync(Query, 0, new RecordingGenerator("other model").GenerateAsync, Ct);
        _ = await Fill(prompt: "other {query}").GetOrAddAsync(Query, 0, new RecordingGenerator("other prompt").GenerateAsync, Ct);

        Assert.Equal("base", await Fill().GetOrAddAsync(Query, 0, Explode, Ct));
        Assert.Equal("index 1", await Fill().GetOrAddAsync(Query, 1, Explode, Ct));
        Assert.Equal("other model", await Fill(model: "other-model").GetOrAddAsync(Query, 0, Explode, Ct));
        Assert.Equal("other prompt", await Fill(prompt: "other {query}").GetOrAddAsync(Query, 0, Explode, Ct));
    }

    [Fact]
    public async Task ATruncatedEntry_IsAMiss_NotATruncatedText()
    {
        // What an interrupted generation run leaves behind. Returning the surviving prefix as if
        // whole is the dangerous case: a truncated hypothetical embeds and searches like any other
        // text, so nothing downstream would ever object.
        _ = await Fill().GetOrAddAsync(Query, 0, new RecordingGenerator(Hypothetical).GenerateAsync, Ct);

        var entry = Assert.Single(EntryFiles());
        var bytes = await File.ReadAllBytesAsync(entry, Ct);
        await File.WriteAllBytesAsync(entry, bytes[..^4], Ct);

        var generator = new RecordingGenerator(Hypothetical);
        var cache = Fill();
        var text = await cache.GetOrAddAsync(Query, 0, generator.GenerateAsync, Ct);

        Assert.Equal(Hypothetical, text);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(1L, cache.Misses);
    }

    [Fact]
    public async Task ACorruptEntry_IsAMiss()
    {
        _ = await Fill().GetOrAddAsync(Query, 0, new RecordingGenerator(Hypothetical).GenerateAsync, Ct);

        var entry = Assert.Single(EntryFiles());
        await File.WriteAllBytesAsync(entry, [0xDE, 0xAD, 0xBE, 0xEF], Ct);

        var generator = new RecordingGenerator(Hypothetical);
        var text = await Fill().GetOrAddAsync(Query, 0, generator.GenerateAsync, Ct);

        Assert.Equal(Hypothetical, text);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task AnEntryWhoseTextBytesWereRewritten_IsAMiss()
    {
        // Intact length, correct magic, correct key — wrong content. A length check alone cannot
        // see this one; the content digest is what makes it a miss.
        _ = await Fill().GetOrAddAsync(Query, 0, new RecordingGenerator(Hypothetical).GenerateAsync, Ct);

        var entry = Assert.Single(EntryFiles());
        var bytes = await File.ReadAllBytesAsync(entry, Ct);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(entry, bytes, Ct);

        var generator = new RecordingGenerator(Hypothetical);
        var text = await Fill().GetOrAddAsync(Query, 0, generator.GenerateAsync, Ct);

        Assert.Equal(Hypothetical, text);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task InRefuseOnMissMode_AMissThrows_AndTheMessageNamesTheKey()
    {
        // The test that matters most. Store the entry once so its key is knowable from the file
        // name, then delete it — the shape a half-finished generation run leaves — and the table
        // run must fail saying which key is missing, not quietly generate a replacement.
        _ = await Fill().GetOrAddAsync(Query, 2, new RecordingGenerator(Hypothetical).GenerateAsync, Ct);
        var entry = Assert.Single(EntryFiles());
        var key = Path.GetFileNameWithoutExtension(entry);
        File.Delete(entry);

        var generator = new RecordingGenerator("must never be produced");
        var cache = Refuse();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrAddAsync(Query, 2, generator.GenerateAsync, Ct));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.Contains(Query, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, generator.Calls);
        Assert.Empty(EntryFiles());
    }

    [Fact]
    public async Task Contains_TracksWhetherAnIntactEntryExists()
    {
        var cache = Fill();
        Assert.False(cache.Contains(Query, 0));

        _ = await cache.GetOrAddAsync(Query, 0, new RecordingGenerator(Hypothetical).GenerateAsync, Ct);
        Assert.True(cache.Contains(Query, 0));
        Assert.False(cache.Contains(Query, 1));

        // A corrupt entry does not count as present, so a resumed generation run regenerates it
        // rather than skipping past it.
        var entry = Assert.Single(EntryFiles());
        await File.WriteAllBytesAsync(entry, [0xDE, 0xAD], Ct);
        Assert.False(cache.Contains(Query, 0));
    }

    [Fact]
    public void Constructor_RejectsABlankModelIdentityOrPromptTemplate()
    {
        // A blank identity or template is the same failure as none at all: every generation shares
        // one key space, and the first generation's text is handed to the second.
        _ = Assert.Throws<ArgumentException>(
            () => new HypotheticalCache(_root, "  ", Prompt, HypotheticalCacheMode.Fill));
        _ = Assert.Throws<ArgumentException>(
            () => new HypotheticalCache(_root, Model, "", HypotheticalCacheMode.Fill));
    }

    [Fact]
    public async Task ANegativeHypothesisIndex_IsRejected()
    {
        var cache = Fill();

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => cache.GetOrAddAsync(Query, -1, Explode, Ct));
    }

    [Fact]
    public async Task AGeneratorReturningBlankText_Throws_RatherThanCaching()
    {
        // An empty hypothetical would embed to a meaningless vector and search like a real one.
        // Refusing to store it keeps the failure at generation time, where the model id and query
        // are still on hand.
        var cache = Fill();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrAddAsync(Query, 0, _ => Task.FromResult("   "), Ct));

        Assert.Empty(EntryFiles());
    }

    [Fact]
    public void EntriesLiveUnderTheCacheRoot_NeverBesideTheCode()
    {
        var cache = Fill();

        Assert.Equal(
            Path.Combine(_root, HypotheticalCache.DirectoryName),
            cache.EntryDirectory,
            StringComparer.Ordinal);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A generator that must never be called, so a silent miss fails loudly.</summary>
    private static readonly Func<CancellationToken, Task<string>> Explode =
        _ => throw new InvalidOperationException(
            "The cache generated a hypothetical it should have read from disk.");

    private HypotheticalCache Fill(string model = Model, string prompt = Prompt) =>
        new(_root, model, prompt, HypotheticalCacheMode.Fill);

    private HypotheticalCache Refuse(string model = Model, string prompt = Prompt) =>
        new(_root, model, prompt, HypotheticalCacheMode.RefuseOnMiss);

    private IReadOnlyList<string> EntryFiles()
    {
        var directory = Path.Combine(_root, HypotheticalCache.DirectoryName);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.hyp", SearchOption.AllDirectories)
            : [];
    }

    /// <summary>
    /// A fake generator that records how often it was asked and returns a fixed text, so text from
    /// the wrong generation is visible rather than merely plausible.
    /// </summary>
    private sealed class RecordingGenerator
    {
        private readonly string _text;

        public RecordingGenerator(string text)
        {
            _text = text;
        }

        public int Calls { get; private set; }

        public Task<string> GenerateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(_text);
        }
    }
}
