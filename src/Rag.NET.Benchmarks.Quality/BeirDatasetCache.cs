using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Resolves a local directory of BEIR datasets, downloading one on demand when it is not already
/// there.
/// <para>
/// Datasets are <b>never vendored into the repository</b>: SciFact alone is several megabytes, the
/// licences are not ours to redistribute under (see
/// <see cref="BeirDatasetDescriptor.Licence"/>), and a checked-in corpus quietly becomes a corpus
/// nobody re-verifies.
/// </para>
/// <para>
/// What arrives is verified before it is trusted, against the MD5 BEIR publishes beside each
/// archive. A truncated or redirected download must fail here and say so: unverified, it extracts
/// to a short corpus, which scores badly, which looks exactly like a retrieval defect — the most
/// expensive possible way to discover a network problem. The download lands on a uniquely named
/// <c>.partial</c> file — one per writer, so parallel test classes cold-starting the same dataset
/// never collide — that is deleted on any verification failure, so a bad fetch can never be
/// mistaken for a cached one on the next run.
/// </para>
/// </summary>
public sealed class BeirDatasetCache
{
    /// <summary>
    /// The environment variable naming the cache directory.
    /// </summary>
    /// <remarks>
    /// The variable is read <b>here</b>, in <c>src/</c>, rather than in the test project, and that
    /// placement is deliberate. <c>RepoConventions</c> asserts in both directions that a test project
    /// reading a <c>RAGNET_</c> variable declares <c>RequiresSecrets</c>, which moves it out of
    /// ci.yml's gating tier and into the advisory nightly job. The loader and metric unit tests need
    /// no cache at all — they hand the loaders an explicit temporary directory — so keeping the read
    /// on this side leaves them where a defect fails a pull request.
    /// </remarks>
    public const string CacheDirectoryVariable = "RAGNET_BEIR_CACHE";

    /// <summary>
    /// Files that must exist for a dataset directory to count as a complete extraction. A directory
    /// holding only some of them is a half-finished download, not a cache hit.
    /// </summary>
    private static readonly string[] RequiredFiles = ["corpus.jsonl", "queries.jsonl"];

    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<BeirDatasetCache>? _logger;

    /// <summary>Creates a cache rooted at <paramref name="cacheDirectory"/>.</summary>
    /// <param name="cacheDirectory">The directory holding one subdirectory per dataset.</param>
    /// <param name="httpClient">
    /// The client used to download archives. Optional; a shared long-timeout client is used
    /// otherwise. Supplying one is how the tests exercise the download and verification paths
    /// without a network.
    /// </param>
    /// <param name="logger">Optional.</param>
    public BeirDatasetCache(
        string cacheDirectory, HttpClient? httpClient = null, ILogger<BeirDatasetCache>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        _cacheDirectory = cacheDirectory;
        _httpClient = httpClient ?? SharedHttpClient;
        _logger = logger;
    }

    /// <summary>Gets the cache root.</summary>
    public string CacheDirectory => _cacheDirectory;

    /// <summary>
    /// Reads the cache directory from <see cref="CacheDirectoryVariable"/>.
    /// </summary>
    /// <returns>
    /// The configured directory, or <see langword="null"/> when the variable is unset or blank — the
    /// signal an env-gated test skips on.
    /// </returns>
    public static string? ResolveCacheDirectoryFromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable(CacheDirectoryVariable);
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    /// <summary>Gets the directory <paramref name="dataset"/> extracts into.</summary>
    /// <param name="dataset">The dataset.</param>
    /// <returns>The directory path, whether or not it exists.</returns>
    public string DirectoryFor(BeirDatasetDescriptor dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        return Path.Combine(_cacheDirectory, dataset.Name);
    }

    /// <summary>Reports whether <paramref name="dataset"/> is already fully extracted.</summary>
    /// <param name="dataset">The dataset.</param>
    /// <returns><see langword="true"/> when every required file and the qrels directory are present.</returns>
    public bool IsPresent(BeirDatasetDescriptor dataset)
    {
        var datasetDirectory = DirectoryFor(dataset);
        if (!Directory.Exists(Path.Combine(datasetDirectory, "qrels")))
        {
            return false;
        }

        foreach (ref readonly var fileName in RequiredFiles.AsSpan())
        {
            if (!File.Exists(Path.Combine(datasetDirectory, fileName)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns the dataset's directory, downloading, verifying and extracting the archive first if
    /// it is not already there.
    /// </summary>
    /// <param name="dataset">The dataset to make available.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The directory <see cref="BeirLoader.Load"/> can be pointed at.</returns>
    /// <exception cref="InvalidDataException">
    /// The archive's length or MD5 does not match what was published, or the extraction did not
    /// produce the files a BEIR dataset must have.
    /// </exception>
    public async Task<string> EnsureAsync(
        BeirDatasetDescriptor dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var datasetDirectory = DirectoryFor(dataset);
        if (IsPresent(dataset))
        {
            if (_logger is not null)
            {
                BeirLog.DatasetAlreadyCached(_logger, dataset.Name, datasetDirectory);
            }

            return datasetDirectory;
        }

        _ = Directory.CreateDirectory(_cacheDirectory);
        var archivePath = Path.Combine(_cacheDirectory, dataset.ArchiveFileName);
        await DownloadAndVerifyAsync(dataset, archivePath, cancellationToken).ConfigureAwait(false);

        // Into the cache root, not into the dataset directory: BEIR archives already carry the
        // dataset name as their single top-level folder.
        ZipFile.ExtractToDirectory(archivePath, _cacheDirectory, overwriteFiles: true);

        if (!IsPresent(dataset))
        {
            throw new InvalidDataException(
                $"'{dataset.ArchiveFileName}' matched its published MD5 but extracting it did not " +
                $"produce '{datasetDirectory}' with corpus.jsonl, queries.jsonl and qrels/. The " +
                "archive layout has changed; loading it would silently score against the wrong files.");
        }

        return datasetDirectory;
    }

    /// <remarks>
    /// The partial file's name carries a fresh GUID — the shape <see cref="EmbeddingCache"/> and
    /// <see cref="HypotheticalCache"/> already use — because xUnit runs test classes in parallel,
    /// and on a cold cache two classes wanting the same dataset both reach here. On one shared
    /// <c>.partial</c> path the second <see cref="File.Create(string)"/> throws
    /// <see cref="IOException"/> — nightly run 30735435427 — while a GUID gives each writer its
    /// own file and asks for no lock, which would only serialise unrelated downloads. When both
    /// then complete, both <see cref="File.Move(string, string, bool)"/> into place and the later
    /// move overwrites the earlier with bytes verified against the same published MD5: one writer
    /// wins, both see a complete archive, and nothing needs to notice it lost. That double move
    /// is correct, not a defect to fix.
    /// </remarks>
    private async Task DownloadAndVerifyAsync(
        BeirDatasetDescriptor dataset, string archivePath, CancellationToken cancellationToken)
    {
        if (_logger is not null)
        {
            BeirLog.DownloadingDataset(_logger, dataset.Name, dataset.ArchiveUrl, _cacheDirectory);
        }

        var partialPath =
            archivePath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".partial";
        long declaredLength;
        long writtenLength;

        using (var response = await _httpClient
            .GetAsync(dataset.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false))
        {
            _ = response.EnsureSuccessStatusCode();
            declaredLength = response.Content.Headers.ContentLength ?? -1;

            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var destination = File.Create(partialPath);
                await using (destination.ConfigureAwait(false))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    writtenLength = destination.Length;
                }
            }
        }

        var actualMd5 = ComputeMd5(partialPath);
        var failure = DescribeFailure(dataset, declaredLength, writtenLength, actualMd5);
        if (failure is not null)
        {
            // Never leave a bad archive where the next run could treat it as cached.
            File.Delete(partialPath);
            throw new InvalidDataException(failure);
        }

        if (_logger is not null)
        {
            BeirLog.ArchiveVerified(_logger, dataset.Name, writtenLength, actualMd5);
        }

        File.Move(partialPath, archivePath, overwrite: true);
    }

    /// <summary>
    /// Describes what is wrong with a downloaded archive, or <see langword="null"/> when it is what
    /// BEIR published.
    /// </summary>
    /// <remarks>
    /// Length is checked before the digest only so the message can distinguish "the transfer was cut
    /// short" from "this is a different file". The MD5 is the check that matters, and it catches the
    /// truncation too; the declared length is absent whenever the response is chunked.
    /// </remarks>
    private static string? DescribeFailure(
        BeirDatasetDescriptor dataset, long declaredLength, long writtenLength, string actualMd5)
    {
        if (declaredLength >= 0 && writtenLength != declaredLength)
        {
            return $"Downloading '{dataset.ArchiveUrl}' produced {writtenLength} bytes but the " +
                $"response declared {declaredLength}. The transfer was cut short. Unverified, this " +
                "extracts to a short corpus that scores badly and reads as a retrieval defect.";
        }

        if (!string.Equals(actualMd5, dataset.ArchiveMd5, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{dataset.ArchiveUrl}' returned {writtenLength} bytes with MD5 {actualMd5}, but " +
                $"BEIR publishes {dataset.ArchiveMd5} for '{dataset.Name}'. Either the download was " +
                "truncated, a proxy or captive portal answered instead of the server, or the " +
                "published archive changed. Any of the three would otherwise surface as a bad " +
                "retrieval score rather than as a bad download.";
        }

        return null;
    }

    /// <summary>
    /// The archive digest, lower-case hex.
    /// </summary>
    /// <remarks>
    /// MD5 because that is the checksum BEIR publishes, and it is used for integrity against
    /// accidental corruption, never as a security boundary: the archive is a public research dataset
    /// fetched over HTTPS, and an attacker able to substitute it could equally substitute the
    /// checksum this is compared against.
    /// </remarks>
    private static string ComputeMd5(string path)
    {
        var stream = File.OpenRead(path);
        using (stream)
        {
            return Convert.ToHexStringLower(MD5.HashData(stream));
        }
    }
}
