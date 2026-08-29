using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="GraphExtractionCache"/> against a fake generator and a temporary directory. <b>No
/// LLM, no network.</b>
/// <para>
/// <b>What this file is mostly about is the directory the entries land in.</b> The cache holds two
/// GraphRAG stages now — extractions and community reports — and it separates them by subdirectory
/// rather than by anything in the key. That choice is only safe if three things hold, and each has a
/// case below: the default is still the extraction directory, so nothing already generated is
/// orphaned; one stage's entries are invisible to the other, so a report can never be served as an
/// extraction; and a refuse-on-miss failure names the directory it looked in, so "the reports were
/// never generated" cannot be mistaken for "the cache is empty".
/// </para>
/// </summary>
public sealed class GraphExtractionCacheTests : IDisposable
{
    private const string Identity = "openai/gpt-4o-mini@t0.0";
    private const string Prompt = "User: Extract entities from the following text:\nA sentence.";
    private const string Response = """{"entities": [{"name": "A", "type": "THING"}]}""";

    private readonly string _root;

    public GraphExtractionCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ragnet-graph-cache-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Two short retries, for the reason HypotheticalCacheTests documents at length: an indexer
        // or a scanner briefly holding a just-written file must not fail a test that ran perfectly.
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

    /// <summary>
    /// A prompt with no caller options must hash to exactly the key it hashed to before options
    /// existed.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes "zero regeneration" a checked claim.</b> All 86,510 entries on disk —
    /// 47,322 answers, 35,176 extractions, 4,012 reports — were written by calls that passed no
    /// options. Appending a zero-length third field to the key buffer would add an int32 length and
    /// a NUL, change every hash, and orphan the lot while appearing to work.
    /// </remarks>
    [Fact]
    public void AKeyWithNoOptions_IsUnchangedFromBeforeOptionsExisted()
    {
        var cache = Fill();

        Assert.Equal(
            "e1c045862bd53afba2af1cbca9efafea06df61495eea5ac03f5e395670814460",
            cache.KeyForTesting("golden-prompt"));
    }

    /// <summary>Two option strings over one prompt are two entries.</summary>
    [Fact]
    public void DifferentOptions_DoNotShareAnEntry()
    {
        var cache = Fill();

        Assert.NotEqual(
            cache.KeyForTesting("p", "maxOutputTokens=150"),
            cache.KeyForTesting("p", "maxOutputTokens=300"),
            StringComparer.Ordinal);
        Assert.NotEqual(
            cache.KeyForTesting("p"),
            cache.KeyForTesting("p", "maxOutputTokens=150"),
            StringComparer.Ordinal);
        Assert.Equal(
            cache.KeyForTesting("p"), cache.KeyForTesting("p", optionsKey: ""), StringComparer.Ordinal);
    }

    [Fact]
    public void WithNoSubdirectoryNamed_EntriesGoWhereTheyAlwaysWent()
    {
        // The compatibility case. Every extraction already on disk lives under this name, and a
        // default that moved would orphan 41,000 paid-for responses without a word.
        var cache = Fill();

        Assert.Equal(
            Path.Combine(_root, GraphExtractionCache.DirectoryName),
            cache.EntryDirectory,
            StringComparer.Ordinal);
    }

    [Fact]
    public void TheReportsDirectory_IsASiblingOfTheExtractionsOne()
    {
        var cache = Fill(subdirectory: GraphExtractionCache.ReportsDirectoryName);

        Assert.Equal(
            Path.Combine(_root, GraphExtractionCache.ReportsDirectoryName),
            cache.EntryDirectory,
            StringComparer.Ordinal);
        Assert.NotEqual(
            GraphExtractionCache.DirectoryName,
            GraphExtractionCache.ReportsDirectoryName,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task AnEntryWrittenInOneDirectory_IsAMissInTheOther()
    {
        // Same identity, same prompt, different stage. Nothing in the key separates the two, so if
        // the directory did not, a community report could be served as an extraction — which parses
        // to an empty graph and is swallowed silently by GraphEntityExtractionBehavior.
        var generator = new RecordingGenerator(Response);
        _ = await Fill().GetOrAddAsync(Prompt, generator.GenerateAsync, cancellationToken: Ct);

        var reports = Fill(subdirectory: GraphExtractionCache.ReportsDirectoryName);
        var other = new RecordingGenerator("a community report");

        var text = await reports.GetOrAddAsync(Prompt, other.GenerateAsync, cancellationToken: Ct);

        Assert.Equal("a community report", text);
        Assert.Equal(1, other.Calls);
        Assert.Equal(1L, reports.Misses);
        _ = Assert.Single(EntryFiles(GraphExtractionCache.DirectoryName));
        _ = Assert.Single(EntryFiles(GraphExtractionCache.ReportsDirectoryName));
    }

    [Fact]
    public async Task EachDirectoryStillReadsBackItsOwnText()
    {
        // The stronger form of the case above: after both directories hold an entry for the same
        // prompt, each hands back its own. One overwriting the other would satisfy the miss
        // assertion and still be wrong.
        _ = await Fill().GetOrAddAsync(Prompt, new RecordingGenerator(Response).GenerateAsync, cancellationToken: Ct);
        _ = await Fill(subdirectory: GraphExtractionCache.ReportsDirectoryName)
            .GetOrAddAsync(Prompt, new RecordingGenerator("a community report").GenerateAsync, cancellationToken: Ct);

        Assert.Equal(Response, await Fill().GetOrAddAsync(Prompt, Explode, cancellationToken: Ct));
        Assert.Equal(
            "a community report",
            await Fill(subdirectory: GraphExtractionCache.ReportsDirectoryName)
                .GetOrAddAsync(Prompt, Explode, cancellationToken: Ct));
    }

    [Fact]
    public async Task ARefuseOnMissFailure_NamesTheKeyAndTheDirectoryItLookedIn()
    {
        // The failure the GraphRAG guard produces on a machine where one stage was generated and
        // the other was not. Without the directory in the message, "the reports were never
        // generated" and "the cache is empty" read identically.
        var reports = Refuse(subdirectory: GraphExtractionCache.ReportsDirectoryName);
        var generator = new RecordingGenerator("must never be produced");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reports.GetOrAddAsync(Prompt, generator.GenerateAsync, cancellationToken: Ct));

        Assert.Contains(
            GraphExtractionCache.ReportsDirectoryName, exception.Message, StringComparison.Ordinal);
        Assert.Contains(Identity, exception.Message, StringComparison.Ordinal);
        Assert.Contains("--stage reports", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, generator.Calls);
        Assert.Empty(EntryFiles(GraphExtractionCache.ReportsDirectoryName));
    }

    [Fact]
    public async Task TheMissingKeyIsTheKeyTheFileWouldHaveHad()
    {
        // Named as the key, not merely as "a key": store the entry so its name is knowable, delete
        // it — the shape a half-finished generation run leaves — and refuse-on-miss must name it.
        _ = await Fill(subdirectory: GraphExtractionCache.ReportsDirectoryName)
            .GetOrAddAsync(Prompt, new RecordingGenerator("a report").GenerateAsync, cancellationToken: Ct);

        var entry = Assert.Single(EntryFiles(GraphExtractionCache.ReportsDirectoryName));
        var key = Path.GetFileNameWithoutExtension(entry);
        File.Delete(entry);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Refuse(subdirectory: GraphExtractionCache.ReportsDirectoryName)
                .GetOrAddAsync(Prompt, Explode, cancellationToken: Ct));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHitInTheReportDirectory_IsServedWithoutGenerating()
    {
        _ = await Fill(subdirectory: GraphExtractionCache.ReportsDirectoryName)
            .GetOrAddAsync(Prompt, new RecordingGenerator("a report").GenerateAsync, cancellationToken: Ct);

        var cache = Refuse(subdirectory: GraphExtractionCache.ReportsDirectoryName);
        var text = await cache.GetOrAddAsync(Prompt, Explode, cancellationToken: Ct);

        Assert.Equal("a report", text);
        Assert.Equal(1L, cache.Hits);
        Assert.Equal(0L, cache.Misses);
        Assert.True(cache.Contains(Prompt));
        Assert.Equal("a report", cache.TryGet(Prompt));
    }

    [Fact]
    public void ABlankSubdirectory_IsRejected()
    {
        // Blank would resolve to the cache root itself, mixing entries in with the datasets and
        // with every other stage — the one placement from which nothing can be counted or cleared.
        _ = Assert.Throws<ArgumentException>(
            () => new GraphExtractionCache(_root, Identity, GraphExtractionCacheMode.Fill, "  "));
        _ = Assert.Throws<ArgumentException>(
            () => new GraphExtractionCache(_root, Identity, GraphExtractionCacheMode.Fill, ""));
        _ = Assert.Throws<ArgumentNullException>(
            () => new GraphExtractionCache(_root, Identity, GraphExtractionCacheMode.Fill, null!));
    }

    [Fact]
    public async Task ADifferentModelIdentity_IsStillAMissInsideOneDirectory()
    {
        // The directory separates the stages; the identity separates the models. Neither subsumes
        // the other, and regenerating the reports under a new model must not hit the old ones.
        _ = await Fill(subdirectory: GraphExtractionCache.ReportsDirectoryName)
            .GetOrAddAsync(Prompt, new RecordingGenerator("mini's report").GenerateAsync, cancellationToken: Ct);

        var newer = new RecordingGenerator("a better model's report");
        var cache = Fill("openai/gpt-4o@t0.0", GraphExtractionCache.ReportsDirectoryName);

        Assert.Equal(
            "a better model's report", await cache.GetOrAddAsync(Prompt, newer.GenerateAsync, cancellationToken: Ct));
        Assert.Equal(2, EntryFiles(GraphExtractionCache.ReportsDirectoryName).Count);
    }

    [Fact]
    public async Task ATruncatedReportEntry_IsAMiss_NotATruncatedReport()
    {
        // What an interrupted report run leaves behind. A truncated report embeds and retrieves
        // like a whole one, so nothing downstream would ever object to it.
        _ = await Fill(subdirectory: GraphExtractionCache.ReportsDirectoryName)
            .GetOrAddAsync(Prompt, new RecordingGenerator("a whole report").GenerateAsync, cancellationToken: Ct);

        var entry = Assert.Single(EntryFiles(GraphExtractionCache.ReportsDirectoryName));
        var bytes = await File.ReadAllBytesAsync(entry, Ct);
        await File.WriteAllBytesAsync(entry, bytes[..^4], Ct);

        var cache = Fill(subdirectory: GraphExtractionCache.ReportsDirectoryName);
        var generator = new RecordingGenerator("a whole report");

        Assert.Equal("a whole report", await cache.GetOrAddAsync(Prompt, generator.GenerateAsync, cancellationToken: Ct));
        Assert.Equal(1, generator.Calls);
        Assert.Equal(1L, cache.Misses);
    }

    [Fact]
    public async Task AGeneratorReturningBlankText_Throws_RatherThanCaching()
    {
        // A blank report is the dangerous one: it embeds to a meaningless vector, retrieves like
        // any other chunk, and reads as a community nobody could summarise.
        var cache = Fill(subdirectory: GraphExtractionCache.ReportsDirectoryName);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrAddAsync(Prompt, _ => Task.FromResult("   "), cancellationToken: Ct));

        Assert.Empty(EntryFiles(GraphExtractionCache.ReportsDirectoryName));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A generator that must never be called, so a silent miss fails loudly.</summary>
    private static readonly Func<CancellationToken, Task<string>> Explode =
        _ => throw new InvalidOperationException(
            "The cache generated a response it should have read from disk.");

    private GraphExtractionCache Fill(
        string identity = Identity, string subdirectory = GraphExtractionCache.DirectoryName) =>
        new(_root, identity, GraphExtractionCacheMode.Fill, subdirectory);

    private GraphExtractionCache Refuse(
        string identity = Identity, string subdirectory = GraphExtractionCache.DirectoryName) =>
        new(_root, identity, GraphExtractionCacheMode.RefuseOnMiss, subdirectory);

    private IReadOnlyList<string> EntryFiles(string subdirectory)
    {
        var directory = Path.Combine(_root, subdirectory);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.gex", SearchOption.AllDirectories)
            : [];
    }

    /// <summary>
    /// A fake generator that records how often it was asked and returns a fixed text, so text from
    /// the wrong stage is visible rather than merely plausible.
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
