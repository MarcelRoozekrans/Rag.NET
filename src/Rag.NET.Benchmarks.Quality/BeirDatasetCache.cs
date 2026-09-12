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

    /// <summary>
    /// Describes a conventional cache that exists but is not referenced by the environment.
    /// </summary>
    /// <returns>
    /// A sentence naming the directory and its <c>env.sh</c>, or <see langword="null"/> when the
    /// environment already points somewhere or no conventional cache is present.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>This does not change what is resolved.</b> It reports; it never becomes a fallback.
    /// Silently adopting a directory the caller did not name would turn a visible skip into an
    /// invisible hour of re-embedding against a corpus nobody asked for.
    /// </para>
    /// <para>
    /// It exists because "unprovisioned" is a dead end that three separate sessions walked into
    /// while the corpus sat at the conventional path with its <c>env.sh</c> beside it.
    /// </para>
    /// </remarks>
    public static string? DescribeUnreferencedConventionalCache() =>
        DescribeUnreferencedConventionalCache(ResolveCacheDirectoryFromEnvironment());

    /// <summary>
    /// Describes a conventional cache that exists but is not referenced by
    /// <paramref name="configuredCacheDirectory"/>.
    /// </summary>
    /// <param name="configuredCacheDirectory">
    /// The value <see cref="ResolveCacheDirectoryFromEnvironment"/> would return, supplied directly
    /// rather than re-read from the environment.
    /// </param>
    /// <returns>
    /// A sentence naming the directory and its <c>env.sh</c>, or <see langword="null"/> when
    /// <paramref name="configuredCacheDirectory"/> is not <see langword="null"/> or no conventional
    /// cache is present.
    /// </returns>
    /// <remarks>
    /// Takes the resolved value as a parameter, rather than reading
    /// <see cref="CacheDirectoryVariable"/> itself, so tests can exercise both branches without
    /// calling <see cref="Environment.SetEnvironmentVariable(string, string)"/> on it. That variable
    /// is process-wide, and roughly 30 sibling tests in the integration test assembly read it
    /// through <c>IsProvisioned</c> / <c>IsDatasetCacheProvisioned</c>; xunit runs test classes in
    /// parallel, so mutating it from a test would risk a sibling observing the temporary value and
    /// skipping, or running, incorrectly. The parameterless overload above is what production code
    /// and <see cref="ResolveCacheDirectoryFromEnvironment"/> callers use; this one exists for tests.
    /// </remarks>
    public static string? DescribeUnreferencedConventionalCache(string? configuredCacheDirectory) =>
        DescribeUnreferencedConventionalCache(
            configuredCacheDirectory,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ragnet-beir"));

    /// <summary>
    /// Describes a conventional cache at <paramref name="conventionalCacheDirectory"/> that exists
    /// but is not referenced by <paramref name="configuredCacheDirectory"/>.
    /// </summary>
    /// <param name="configuredCacheDirectory">
    /// The value <see cref="ResolveCacheDirectoryFromEnvironment"/> would return.
    /// </param>
    /// <param name="conventionalCacheDirectory">
    /// The directory to check for, in place of the real <c>~/.cache/ragnet-beir</c>.
    /// </param>
    /// <returns>
    /// A sentence naming the directory and its <c>env.sh</c>, or <see langword="null"/> when
    /// <paramref name="configuredCacheDirectory"/> is not <see langword="null"/> or
    /// <paramref name="conventionalCacheDirectory"/> does not exist.
    /// </returns>
    /// <remarks>
    /// Takes the conventional root as a parameter, rather than hard-coding
    /// <c>~/.cache/ragnet-beir</c>, for the same reason the environment value above is a parameter
    /// rather than a re-read of <see cref="CacheDirectoryVariable"/>: it lets a test put both
    /// sentence-forming branches — "source the env.sh" and "set the variable" — and the "not
    /// present" branch under a temporary directory it controls, deterministically, on any machine,
    /// rather than depending on whether that machine happens to have the real directory on disk. The
    /// two-parameter overload above is what production code uses; this one exists for tests.
    /// </remarks>
    public static string? DescribeUnreferencedConventionalCache(
        string? configuredCacheDirectory, string conventionalCacheDirectory)
    {
        if (configuredCacheDirectory is not null)
        {
            return null;
        }

        if (!Directory.Exists(conventionalCacheDirectory))
        {
            return null;
        }

        var envScript = Path.Combine(conventionalCacheDirectory, "env.sh");

        return File.Exists(envScript)
            ? $" A cache is already present at '{conventionalCacheDirectory}' and nothing points " +
              $"at it: source '{envScript}' to use it."
            : $" A cache directory is already present at '{conventionalCacheDirectory}' and " +
              $"nothing points at it: set {CacheDirectoryVariable} to it to use it.";
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
    /// <returns>
    /// <see langword="true"/> when every required file and the qrels directory are present, and
    /// the dataset's source, if it has one, finds nothing of its own missing beside them
    /// (<see cref="IBeirDatasetSource.IsComplete"/>).
    /// </returns>
    public bool IsPresent(BeirDatasetDescriptor dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var datasetDirectory = DirectoryFor(dataset);
        return IsExtractedAt(datasetDirectory) && (dataset.Source?.IsComplete(datasetDirectory) ?? true);
    }

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
    /// The source refused what it acquired — a length or digest that does not match what was
    /// published, a shape it could not convert — or the acquisition returned without producing the
    /// files a BEIR dataset must have.
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
            // Named for the dataset rather than for a zip. This message used to say the archive
            // matched its MD5 but extracted wrongly, which was true while every source was
            // BeirArchiveSource and false the moment one downloaded no archive at all — a
            // conversion that produced nothing would have been reported as a bad extraction.
            throw new InvalidDataException(
                $"Acquiring '{dataset.Name}' returned without producing '{datasetDirectory}' with " +
                "corpus.jsonl, queries.jsonl and qrels/. Its source establishes that layout however " +
                "the dataset is published — by extracting an archive, by converting files published " +
                "in some other shape — and this one did not, so what it was given has changed " +
                "shape. Loading it would silently score against the wrong files.");
        }

        return datasetDirectory;
    }
}
