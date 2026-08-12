using Microsoft.Extensions.Logging;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Resolves a local directory of BEIR datasets, acquiring one on demand when it is not already
/// there.
/// <para>
/// Datasets are <b>never vendored into the repository</b>: SciFact alone is several megabytes, the
/// licences are not ours to redistribute under (see
/// <see cref="BeirDatasetDescriptor.Licence"/>), and a checked-in corpus quietly becomes a corpus
/// nobody re-verifies.
/// </para>
/// <para>
/// <b>How a dataset arrives is the descriptor's business, not this cache's.</b> Acquisition goes
/// through <see cref="IBeirDatasetSource"/>, whose postcondition is a directory in BEIR's layout;
/// <see cref="BeirArchiveSource"/> satisfies it by downloading a zip and verifying it against the
/// MD5 BEIR publishes, which is what every descriptor that names no source gets. A dataset
/// published in some other shape satisfies the same postcondition by converting, and everything
/// after this class — <see cref="BeirLoader"/>, the metrics, the sidecars — reads a directory and
/// never learns which happened.
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
    public bool IsPresent(BeirDatasetDescriptor dataset) => IsExtractedAt(DirectoryFor(dataset));

    /// <summary>
    /// Reports whether a directory holds a complete dataset — the postcondition
    /// <see cref="IBeirDatasetSource"/> exists to establish.
    /// </summary>
    /// <param name="datasetDirectory">The directory to inspect.</param>
    /// <returns><see langword="true"/> when every required file and the qrels directory are present.</returns>
    /// <remarks>
    /// Taking a directory rather than a descriptor is what lets a source check its own work: the
    /// source is told where the dataset must land and nothing else, so this is the one question it
    /// can ask, and cache and source cannot disagree about the answer.
    /// </remarks>
    internal static bool IsExtractedAt(string datasetDirectory)
    {
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
    /// Returns the dataset's directory, acquiring it through the dataset's
    /// <see cref="BeirDatasetDescriptor.Source"/> first if it is not already there.
    /// </summary>
    /// <param name="dataset">The dataset to make available.</param>
    /// <param name="cancellationToken">Cancels the acquisition.</param>
    /// <returns>The directory <see cref="BeirLoader.Load"/> can be pointed at.</returns>
    /// <exception cref="InvalidDataException">
    /// The archive's length or MD5 does not match what was published, or the acquisition did not
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

        if (Directory.Exists(datasetDirectory))
        {
            // A half-extracted leftover from an interrupted run. Deleted here, before the download,
            // so that publication never has to decide between "stale junk to replace" and "another
            // caller's fresh, complete win to keep": once downloads are running, a dataset directory
            // that appears is always a rival's complete extraction.
            Directory.Delete(datasetDirectory, recursive: true);
        }

        _ = Directory.CreateDirectory(_cacheDirectory);

        // Null means the normal thing: a BEIR-published zip, verified against the MD5 BEIR
        // publishes for it. A descriptor naming its own source is one BEIR does not publish, and
        // the only thing this class asks of either is the postcondition checked directly below.
        var source = dataset.Source ??
            new BeirArchiveSource(dataset, _cacheDirectory, _httpClient, _logger);

        await source.PrepareAsync(datasetDirectory, cancellationToken).ConfigureAwait(false);

        if (!IsPresent(dataset))
        {
            throw new InvalidDataException(
                $"'{dataset.ArchiveFileName}' matched its published MD5 but extracting it did not " +
                $"produce '{datasetDirectory}' with corpus.jsonl, queries.jsonl and qrels/. The " +
                "archive layout has changed; loading it would silently score against the wrong files.");
        }

        return datasetDirectory;
    }
}
