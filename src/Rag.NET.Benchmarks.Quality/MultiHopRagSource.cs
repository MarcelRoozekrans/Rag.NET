using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Satisfies <see cref="IBeirDatasetSource"/> for MultiHop-RAG, which BEIR does not publish: two
/// JSON files on HuggingFace, downloaded, verified, and converted into BEIR's layout.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pinned to a revision, not to a branch.</b> MultiHop-RAG is a live repository whose files can
/// be re-published under the same names; a figure measured against "whatever <c>main</c> held that
/// week" is not reproducible and would not announce itself as unreproducible. The revision below,
/// with the two lengths and digests measured from it, is what makes a changed upstream a loud
/// failure instead of a slightly different number.
/// </para>
/// <para>
/// <b>The download is separate from the conversion, on purpose.</b> This class fetches and verifies
/// two files and knows nothing about BEIR's layout; <see cref="MultiHopRagConversion"/> reads two
/// files off disk and knows nothing about HTTP. They fail for unrelated reasons — a captive portal
/// against a changed schema — and only one of them needs a network, which is what lets the half
/// carrying all the judgement be pinned by unit tests against synthetic fixtures.
/// </para>
/// </remarks>
public sealed class MultiHopRagSource : IBeirDatasetSource
{
    /// <summary>
    /// The name the dataset is cached and logged under — and therefore the directory it lands in.
    /// </summary>
    /// <remarks>
    /// A constant so the descriptor that names this source and the log lines this source writes
    /// cannot drift apart; every BEIR dataset gets its name from the archive's top-level folder,
    /// and MultiHop-RAG has no archive to take one from.
    /// </remarks>
    public const string DatasetName = "multihop-rag";

    /// <summary>The pinned upstream revision every published figure is measured against.</summary>
    public const string Revision = "71ac0d0bd1f951d2d6b70311f7d2ae404e1ffa82";

    /// <summary>The published article corpus.</summary>
    public const string CorpusFileName = "corpus.json";

    /// <summary>The published queries, answers and evidence.</summary>
    public const string QueriesFileName = "MultiHopRAG.json";

    private const long CorpusLength = 6785567;
    private const string CorpusMd5 = "9b81a85a6acbe0a452b9d51368a2ce87";
    private const long QueriesLength = 5171312;
    private const string QueriesMd5 = "7408d2a79e977d9c6d1641ac39dc3310";

    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>Creates the source.</summary>
    /// <param name="httpClient">
    /// The client used to download the two files. Optional; a shared long-timeout client is used
    /// otherwise, because a descriptor is a <see langword="static"/> field constructed long before
    /// any cache exists and has nothing to hand it.
    /// </param>
    /// <param name="logger">Optional.</param>
    public MultiHopRagSource(HttpClient? httpClient = null, ILogger? logger = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _logger = logger;
    }

    /// <summary>Gets the pinned <c>corpus.json</c>.</summary>
    public static Uri CorpusUrl { get; } = FileUrl(CorpusFileName);

    /// <summary>Gets the pinned <c>MultiHopRAG.json</c>.</summary>
    public static Uri QueriesUrl { get; } = FileUrl(QueriesFileName);

    /// <summary>
    /// Gets what the pinned revision actually contains, measured from the two files on 2026-08-12.
    /// </summary>
    /// <remarks>
    /// 609 articles, all titled. 2,556 queries, of which 301 are <c>null_query</c> records with an
    /// empty <c>evidence_list</c> and the answer "Insufficient information." — written out like any
    /// other query and judged by nothing. The remaining 2,255 carry 6,084 evidence rows that
    /// collapse to 5,908 distinct (query, document) pairs, 2 to 4 per query.
    /// </remarks>
    public static MultiHopRagCounts PublishedCounts { get; } = new(
        DocumentCount: 609,
        QueryCount: 2556,
        JudgedQueryCount: 2255,
        JudgementCount: 5908);

    /// <summary>
    /// How the 2,556 queries split by <c>question_type</c> at the pinned revision: 816 inference,
    /// 856 comparison, 583 temporal, 301 null.
    /// </summary>
    /// <remarks>
    /// The paper's Table 1 figures (arXiv 2401.15391, read 2026-08-15), and they sum to 2,556 —
    /// the null count is also the difference between <see cref="MultiHopRagCounts.QueryCount"/>
    /// and <see cref="MultiHopRagCounts.JudgedQueryCount"/> above, measured independently by the
    /// conversion since 2026-08-12. The conversion asserts all four, so they are checked against
    /// the file every time rather than trusted from the paper.
    /// </remarks>
    public static MultiHopRagQuestionTypeCounts PublishedQuestionTypeCounts { get; } = new(
        Inference: 816,
        Comparison: 856,
        Temporal: 583,
        Null: 301);

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">
    /// A downloaded file's length or MD5 does not match the pinned revision, or the conversion did
    /// not produce what <see cref="PublishedCounts"/> says the revision holds.
    /// </exception>
    public async Task PrepareAsync(string datasetDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetDirectory);

        // Beside the dataset directory rather than inside it: the two JSON files are the input to
        // the conversion, not part of the layout BeirLoader reads, and a GUID in the name gives
        // every writer its own scratch so two test classes cold-starting the same dataset in
        // parallel never collide.
        var scratchDirectory = datasetDirectory + "." +
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".downloading";

        try
        {
            _ = Directory.CreateDirectory(scratchDirectory);

            var corpusPath = await DownloadAndVerifyAsync(
                CorpusUrl, CorpusFileName, CorpusLength, CorpusMd5, scratchDirectory, cancellationToken)
                .ConfigureAwait(false);

            var queriesPath = await DownloadAndVerifyAsync(
                QueriesUrl, QueriesFileName, QueriesLength, QueriesMd5, scratchDirectory, cancellationToken)
                .ConfigureAwait(false);

            await MultiHopRagConversion
                .ConvertAsync(
                    corpusPath, queriesPath, datasetDirectory, PublishedCounts,
                    PublishedQuestionTypeCounts, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(scratchDirectory))
            {
                // Delete on every path out, so a rejected download can never be mistaken for a
                // cached one on the next run.
                Directory.Delete(scratchDirectory, recursive: true);
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The gold-answer sidecar (<see cref="MultiHopRagAnswers.FileName"/>) is written by the same
    /// conversion that writes the three BEIR files, so a directory without it was converted before
    /// the sidecar existed and is re-converted once, from the same pinned revision.
    /// </remarks>
    public bool IsComplete(string datasetDirectory) => MultiHopRagAnswers.IsPresentAt(datasetDirectory);

    private static Uri FileUrl(string fileName) => new(
        "https://huggingface.co/datasets/yixuantt/MultiHopRAG/resolve/" + Revision + "/" + fileName);

    /// <summary>
    /// Downloads one published file into the scratch directory and verifies it against the pinned
    /// revision.
    /// </summary>
    /// <remarks>
    /// What arrives is verified before it is trusted, for the same reason
    /// <see cref="BeirArchiveSource"/> verifies its archive: a truncated or redirected download
    /// converts to a short corpus, which scores badly, which looks exactly like a retrieval defect.
    /// Unlike BEIR, MultiHop-RAG publishes no checksum, so the length and digest here were measured
    /// from the pinned revision and are what pins it.
    /// </remarks>
    /// <returns>The verified file's path.</returns>
    private async Task<string> DownloadAndVerifyAsync(
        Uri url,
        string fileName,
        long publishedLength,
        string publishedMd5,
        string scratchDirectory,
        CancellationToken cancellationToken)
    {
        if (_logger is not null)
        {
            BeirLog.DownloadingDataset(_logger, DatasetName, url, scratchDirectory);
        }

        var path = Path.Combine(scratchDirectory, fileName);
        long declaredLength;
        long writtenLength;

        using (var response = await _httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false))
        {
            _ = response.EnsureSuccessStatusCode();
            declaredLength = response.Content.Headers.ContentLength ?? -1;

            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var destination = File.Create(path);
                await using (destination.ConfigureAwait(false))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    writtenLength = destination.Length;
                }
            }
        }

        var actualMd5 = ComputeMd5(path);
        var failure = DescribeFailure(url, declaredLength, writtenLength, publishedLength, publishedMd5, actualMd5);
        if (failure is not null)
        {
            throw new InvalidDataException(failure);
        }

        if (_logger is not null)
        {
            BeirLog.ArchiveVerified(_logger, DatasetName + "/" + fileName, writtenLength, actualMd5);
        }

        return path;
    }

    /// <summary>
    /// Describes what is wrong with a downloaded file, or <see langword="null"/> when it is what the
    /// pinned revision publishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Length is checked before the digest so the message can distinguish "the transfer was cut
    /// short" from "this is a different file", which <see cref="BeirArchiveSource"/> also does and
    /// for the same reason: the two send whoever reads the message to entirely different places —
    /// the network, or upstream. The digest cannot make that distinction on its own, because a
    /// truncated body fails it exactly as a re-published one does.
    /// </para>
    /// <para>
    /// The declared length is the response's, and it is absent whenever the response is chunked;
    /// the pinned length is the revision's, and it is what a complete transfer of a re-published
    /// file would disagree with. They are different claims and both are checked — the digest last,
    /// because it is the check that actually matters and catches everything the other two would.
    /// </para>
    /// </remarks>
    private static string? DescribeFailure(
        Uri url,
        long declaredLength,
        long writtenLength,
        long publishedLength,
        string publishedMd5,
        string actualMd5)
    {
        if (declaredLength >= 0 && writtenLength != declaredLength)
        {
            return $"Downloading '{url}' produced " +
                $"{writtenLength.ToString(CultureInfo.InvariantCulture)} bytes but the response " +
                $"declared {declaredLength.ToString(CultureInfo.InvariantCulture)}. The transfer " +
                "was cut short. Unverified, this converts to a short corpus that scores badly and " +
                "reads as a retrieval defect.";
        }

        if (writtenLength != publishedLength ||
            !string.Equals(actualMd5, publishedMd5, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{url}' returned {writtenLength.ToString(CultureInfo.InvariantCulture)} bytes " +
                $"with MD5 {actualMd5}, but revision {Revision} publishes " +
                $"{publishedLength.ToString(CultureInfo.InvariantCulture)} bytes with MD5 " +
                $"{publishedMd5}. Either a proxy or captive portal answered instead of the server, " +
                "or the dataset was re-published under the same name. Either would otherwise " +
                "surface as a retrieval score computed from the wrong data rather than as a bad " +
                "download.";
        }

        return null;
    }

    /// <summary>
    /// The file digest, lower-case hex. MD5 for integrity against accidental corruption, never as a
    /// security boundary — see <see cref="BeirArchiveSource"/>, which says the same thing at length.
    /// </summary>
    private static string ComputeMd5(string path)
    {
        var stream = File.OpenRead(path);
        using (stream)
        {
            return Convert.ToHexStringLower(MD5.HashData(stream));
        }
    }
}
