using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Satisfies <see cref="IBeirDatasetSource"/> the way BEIR itself publishes: download the zip,
/// verify it against the published MD5, extract it.
/// </summary>
/// <remarks>
/// <para>
/// What arrives is verified before it is trusted, against the MD5 BEIR publishes beside each
/// archive. A truncated or redirected download must fail here and say so: unverified, it extracts
/// to a short corpus, which scores badly, which looks exactly like a retrieval defect — the most
/// expensive possible way to discover a network problem. The download lands on a uniquely named
/// <c>.partial</c> file — one per writer, so parallel test classes cold-starting the same dataset
/// never collide — that is deleted on any verification failure, so a bad fetch can never be
/// mistaken for a cached one on the next run.
/// </para>
/// <para>
/// This is <see cref="BeirDatasetCache"/>'s original acquisition path, moved unchanged behind the
/// interface. Nothing about it is generic to "a dataset": every line of it is about a zip with a
/// published checksum, which is exactly why a dataset published as something other than a zip needs
/// its own implementation rather than a flag through this one.
/// </para>
/// </remarks>
internal sealed class BeirArchiveSource : IBeirDatasetSource
{
    private readonly BeirDatasetDescriptor _dataset;
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>Creates a source for one dataset's published archive.</summary>
    /// <param name="dataset">The dataset, carrying the archive's URL and published MD5.</param>
    /// <param name="cacheDirectory">
    /// The directory the archive and its <c>.partial</c> are written to — the cache root, beside the
    /// dataset directory rather than inside it, because the zip outlives no extraction and is not
    /// part of the layout <see cref="BeirLoader"/> reads.
    /// </param>
    /// <param name="httpClient">The client used to download the archive.</param>
    /// <param name="logger">Optional.</param>
    public BeirArchiveSource(
        BeirDatasetDescriptor dataset, string cacheDirectory, HttpClient httpClient, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(httpClient);

        _dataset = dataset;
        _cacheDirectory = cacheDirectory;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">
    /// The archive's length or MD5 does not match what was published.
    /// </exception>
    public async Task PrepareAsync(string datasetDirectory, CancellationToken cancellationToken = default)
    {
        var archivePath = Path.Combine(_cacheDirectory, _dataset.ArchiveFileName);
        var partialPath =
            await DownloadAndVerifyAsync(_dataset, archivePath, cancellationToken).ConfigureAwait(false);

        try
        {
            await ExtractIntoPlaceAsync(_dataset, partialPath, datasetDirectory, cancellationToken)
                .ConfigureAwait(false);

            // Published only now, after extraction — and extraction reads the caller's own partial,
            // never this shared name — so no caller ever holds the archive open. Two callers
            // publishing concurrently is a pair of renames onto a closed file: the later one wins
            // with bytes that verified against the same published MD5. On Windows the same rename
            // against a file a rival was still extracting from would be an access-denied error.
            await PublishRename.ReplaceFileAsync(partialPath, archivePath, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                // Extraction or publication failed: keep the delete-on-failure discipline — no
                // partial left where a later run could treat it as anything.
                File.Delete(partialPath);
            }
        }
    }

    /// <summary>
    /// Downloads the archive to a uniquely named partial file, verifies it, and returns the
    /// partial's path — still under its unique name, so the caller owns it exclusively.
    /// </summary>
    /// <remarks>
    /// The partial file's name carries a fresh GUID — the shape <see cref="EmbeddingCache"/> and
    /// <see cref="HypotheticalCache"/> already use — because xUnit runs test classes in parallel,
    /// and on a cold cache two classes wanting the same dataset both reach here. On one shared
    /// <c>.partial</c> path the second <see cref="File.Create(string)"/> throws
    /// <see cref="IOException"/> — nightly run 30735435427 — while a GUID gives each writer its
    /// own file and asks for no lock, which would only serialise unrelated downloads. This is the
    /// first of two same-shaped races on the cold path: <see cref="ExtractIntoPlaceAsync"/> is the
    /// second, and fixing this one alone only moves the collision one step later. Same cure both
    /// times — work under a unique name, rename into the shared one.
    /// </remarks>
    private async Task<string> DownloadAndVerifyAsync(
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

        return partialPath;
    }

    /// <summary>
    /// Extracts a verified archive into the dataset's directory, via a uniquely named staging
    /// directory that is renamed into place.
    /// </summary>
    /// <remarks>
    /// The download and the extraction are two distinct races with the same shape, and fixing one
    /// does not fix the other. With the partial-file race fixed, two callers finishing their
    /// downloads together still collided twice over: publishing the shared archive name while the
    /// other caller held it open for reading, and extracting entries into the shared dataset
    /// directory through exclusive file handles. So extraction reads the caller's own verified
    /// partial — never the shared archive path — and lands in a GUID-named staging directory
    /// beside the dataset, renamed into place only when complete: the dataset directory becomes
    /// visible atomically or not at all, and no third caller can observe a half-populated
    /// extraction. The staging directory is deleted on every path out, keeping the delete-on-failure
    /// discipline the download already has.
    /// </remarks>
    private static async Task ExtractIntoPlaceAsync(
        BeirDatasetDescriptor dataset, string verifiedArchivePath, string datasetDirectory,
        CancellationToken cancellationToken)
    {
        var stagingDirectory = datasetDirectory + "." +
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".extracting";

        try
        {
            ZipFile.ExtractToDirectory(verifiedArchivePath, stagingDirectory);

            // BEIR archives carry the dataset name as their single top-level folder. When the
            // layout has changed and that folder is absent, nothing is published, and the caller's
            // IsPresent check turns it into the InvalidDataException that names the problem.
            var stagedDatasetDirectory = Path.Combine(stagingDirectory, dataset.Name);
            if (Directory.Exists(stagedDatasetDirectory))
            {
                await PublishExtractedDatasetAsync(
                    stagedDatasetDirectory, datasetDirectory, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    /// <summary>Renames the staged, complete dataset directory into place.</summary>
    /// <remarks>
    /// <para>
    /// When two callers both finish extracting, the first rename wins and the loser's rename
    /// throws <see cref="IOException"/> because the destination now exists. Losing is success:
    /// the winner's directory came from an archive verified against the same published MD5, so
    /// the loser keeps it, lets its own staging be discarded, and resolves the same directory.
    /// Do not "fix" the swallowed exception — the filter re-checks
    /// <see cref="BeirDatasetCache.IsExtractedAt"/>, so a rename that failed for any other reason
    /// still throws.
    /// </para>
    /// <para>
    /// The winner's rename has its own Windows-only hazard, external to this process: NTFS
    /// refuses to rename a directory while any handle is open on anything beneath it, whatever
    /// sharing the holder asked for — and every file beneath this one was written milliseconds
    /// ago, which is precisely when an on-access scanner opens it. The retry below is for that
    /// measured, transient denial; <see cref="PublishRename"/> carries the measurements. It
    /// cannot mask a handle this process leaked — our own handle would outlive every attempt and
    /// the final throw would still surface it — and it cannot mask the lost race, because the
    /// <c>!Directory.Exists</c> filter refuses to retry once a rival's dataset directory exists.
    /// </para>
    /// </remarks>
    private static async Task PublishExtractedDatasetAsync(
        string stagedDatasetDirectory, string datasetDirectory, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (BeirDatasetCache.IsExtractedAt(datasetDirectory))
            {
                return;
            }

            try
            {
                Directory.Move(stagedDatasetDirectory, datasetDirectory);
                return;
            }
            catch (IOException) when (BeirDatasetCache.IsExtractedAt(datasetDirectory))
            {
                // Lost a photo finish to a rival whose archive verified against the same MD5.
                return;
            }
            catch (IOException) when (
                attempt < PublishRename.TransientDenialRetryLimit && !Directory.Exists(datasetDirectory))
            {
                // The destination does not exist, so this is not the lost race above: it is the
                // scanner-held source measured on PublishRename. Observed denials clear within
                // single-digit milliseconds; past the limit the exception propagates and the
                // caller's delete-on-failure discipline still runs.
                await Task.Delay(PublishRename.TransientDenialSettleTime, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
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
