using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins <see cref="BeirDatasetCache"/>'s download-and-verify path <b>without a network</b>: the
/// archive is built in memory and served by a stub handler, so the truncation and checksum failures
/// are exercised deterministically rather than hoped for.
/// <para>
/// Verification is the point of this file. Unverified, a truncated or intercepted download extracts
/// to a short corpus, which scores badly, which reads as a retrieval defect — the most expensive
/// possible way to discover a network problem.
/// </para>
/// </summary>
public sealed class BeirDatasetCacheTests : IDisposable
{
    private const string CorpusJsonl =
        """{"_id": "d1", "title": "T", "text": "Body.", "metadata": {}}""";

    private const string QueriesJsonl =
        """{"_id": "q1", "text": "A claim.", "metadata": {}}""";

    private const string QrelsTsv = "query-id\tcorpus-id\tscore\nq1\td1\t1\n";

    private readonly string _cacheDirectory;

    public BeirDatasetCacheTests() =>
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "ragnet-beir-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    [Fact]
    public void CacheDirectoryVariable_IsTheNameNightlyWillSupply()
    {
        // Named as a constant so the parity test and the workflow cannot drift apart, and read inside
        // src/ rather than from a test project — see the remarks on the constant for why that keeps
        // the unit suites out of the nightly tier.
        Assert.Equal("RAGNET_BEIR_CACHE", BeirDatasetCache.CacheDirectoryVariable, StringComparer.Ordinal);
    }

    [Fact]
    public void IsPresent_AnEmptyCache_IsFalse()
    {
        var cache = new BeirDatasetCache(_cacheDirectory, new HttpClient(new ExplodingHandler()));

        Assert.False(cache.IsPresent(BeirDatasetDescriptor.SciFact));
    }

    [Fact]
    public void IsPresent_ADirectoryMissingOneRequiredFile_IsFalse()
    {
        // A half-finished extraction must not read as a cache hit: the run would score against a
        // dataset that is missing part of itself.
        var directory = Path.Combine(_cacheDirectory, "scifact");
        _ = Directory.CreateDirectory(Path.Combine(directory, "qrels"));
        File.WriteAllText(Path.Combine(directory, "corpus.jsonl"), CorpusJsonl);

        var cache = new BeirDatasetCache(_cacheDirectory, new HttpClient(new ExplodingHandler()));

        Assert.False(cache.IsPresent(BeirDatasetDescriptor.SciFact));
    }

    [Fact]
    public async Task EnsureAsync_ADatasetAlreadyExtracted_NeverTouchesTheNetwork()
    {
        var directory = Path.Combine(_cacheDirectory, "tiny");
        _ = Directory.CreateDirectory(Path.Combine(directory, "qrels"));
        File.WriteAllText(Path.Combine(directory, "corpus.jsonl"), CorpusJsonl);
        File.WriteAllText(Path.Combine(directory, "queries.jsonl"), QueriesJsonl);
        File.WriteAllText(Path.Combine(directory, "qrels", "test.tsv"), QrelsTsv);

        // The handler throws on any request, so this test fails loudly if the cache re-downloads.
        var cache = new BeirDatasetCache(_cacheDirectory, new HttpClient(new ExplodingHandler()));

        var resolved = await cache.EnsureAsync(Descriptor(Md5Of([1])), TestContext.Current.CancellationToken);

        Assert.Equal(directory, resolved, StringComparer.Ordinal);
    }

    [Fact]
    public async Task EnsureAsync_AnArchiveMatchingItsPublishedChecksum_ExtractsAndLoads()
    {
        var archive = BuildArchive();
        var cache = new BeirDatasetCache(_cacheDirectory, new HttpClient(new ArchiveHandler(archive, archive.Length)));

        var directory = await cache.EnsureAsync(
            Descriptor(Md5Of(archive)), TestContext.Current.CancellationToken);

        var dataset = BeirLoader.Load(directory);

        Assert.Single(dataset.Documents);
        Assert.Equal(1, dataset.JudgedQueryCount);
    }

    [Fact]
    public async Task EnsureAsync_AnArchiveWhoseChecksumDoesNotMatch_ThrowsNamingBothDigests()
    {
        // A truncated body: the bytes are a prefix of the real archive, so it is still a plausible
        // file. Only the checksum distinguishes it, and without that check it would extract to a
        // short corpus and be blamed on retrieval.
        var archive = BuildArchive();
        var truncated = archive[..(archive.Length / 2)];
        var published = Md5Of(archive);

        var cache = new BeirDatasetCache(
            _cacheDirectory, new HttpClient(new ArchiveHandler(truncated, truncated.Length)));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.EnsureAsync(Descriptor(published), TestContext.Current.CancellationToken));

        Assert.Contains(published, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Md5Of(truncated), ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureAsync_AFailedVerification_LeavesNothingBehindForTheNextRunToTrust()
    {
        // The failure that matters most is the second run, not the first. A rejected archive left on
        // disk would be picked up as a cache hit and the loud failure would happen exactly once.
        var archive = BuildArchive();
        var truncated = archive[..(archive.Length / 2)];

        var cache = new BeirDatasetCache(
            _cacheDirectory, new HttpClient(new ArchiveHandler(truncated, truncated.Length)));

        _ = await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.EnsureAsync(Descriptor(Md5Of(archive)), TestContext.Current.CancellationToken));

        Assert.False(cache.IsPresent(Descriptor(Md5Of(archive))));
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.partial"));
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.zip"));
    }

    [Fact]
    public async Task EnsureAsync_ABodyShorterThanTheDeclaredContentLength_SaysTheTransferWasCutShort()
    {
        // Distinguished from a checksum mismatch on purpose: "the transfer was cut short" and "this
        // is a different file" send whoever reads the message to different places.
        var archive = BuildArchive();
        var truncated = archive[..(archive.Length / 2)];

        var cache = new BeirDatasetCache(
            _cacheDirectory, new HttpClient(new ArchiveHandler(truncated, archive.Length)));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.EnsureAsync(Descriptor(Md5Of(archive)), TestContext.Current.CancellationToken));

        Assert.Contains("cut short", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectoryFor_PutsEachDatasetInItsOwnSubdirectory()
    {
        var cache = new BeirDatasetCache(_cacheDirectory, new HttpClient(new ExplodingHandler()));

        Assert.Equal(
            Path.Combine(_cacheDirectory, "scifact"),
            cache.DirectoryFor(BeirDatasetDescriptor.SciFact),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// A descriptor for the in-memory archive: the same shape as
    /// <see cref="BeirDatasetDescriptor.SciFact"/>, with whatever checksum the test wants to claim
    /// was published.
    /// </summary>
    private static BeirDatasetDescriptor Descriptor(string md5) => new(
        "tiny",
        new Uri("https://example.invalid/datasets/tiny.zip"),
        md5,
        "test fixture",
        DocumentCount: 1,
        QueryCount: 1,
        TestQueryCount: 1);

    /// <summary>
    /// A zip in BEIR's layout: one top-level folder named after the dataset, holding
    /// <c>corpus.jsonl</c>, <c>queries.jsonl</c> and <c>qrels/test.tsv</c>.
    /// </summary>
    private static byte[] BuildArchive()
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "tiny/corpus.jsonl", CorpusJsonl);
            AddEntry(archive, "tiny/queries.jsonl", QueriesJsonl);
            AddEntry(archive, "tiny/qrels/test.tsv", QrelsTsv);
        }

        return buffer.ToArray();
    }

    private static void AddEntry(ZipArchive archive, string entryName, string content)
    {
        using var stream = archive.CreateEntry(entryName).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string Md5Of(byte[] content) => Convert.ToHexStringLower(MD5.HashData(content));

    /// <summary>Serves a fixed body, optionally declaring a Content-Length that disagrees with it.</summary>
    private sealed class ArchiveHandler(byte[] body, long declaredLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentLength = declaredLength;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Fails any request, so "this path does not use the network" is asserted, not assumed.</summary>
    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "BeirDatasetCache made an HTTP request on a path that must not touch the network.");
    }
}
