using Microsoft.Extensions.Logging;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Source-generated logging for the dataset cache.
/// </summary>
/// <remarks>
/// Parameters stay in the same order as their placeholders: when the two diverge, the
/// <c>LoggerMessage</c> generator emits a state type whose <c>GetEnumerator</c> returns a reference
/// type, HLQ006 rejects it, and warnings are errors here.
/// </remarks>
internal static partial class BeirLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "BEIR dataset '{DatasetName}' is already extracted at '{DatasetDirectory}'; nothing was downloaded")]
    internal static partial void DatasetAlreadyCached(ILogger logger, string datasetName, string datasetDirectory);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Downloading BEIR dataset '{DatasetName}' from '{ArchiveUrl}' into '{CacheDirectory}'; datasets are cached, never vendored into the repository")]
    internal static partial void DownloadingDataset(
        ILogger logger, string datasetName, Uri archiveUrl, string cacheDirectory);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Verified BEIR dataset '{DatasetName}': {ByteCount} bytes, MD5 {ArchiveMd5} matches the checksum BEIR publishes")]
    internal static partial void ArchiveVerified(ILogger logger, string datasetName, long byteCount, string archiveMd5);
}
