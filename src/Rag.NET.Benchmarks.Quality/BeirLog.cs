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
        EventId = 1932655013, EventName = "dataset_already_cached",
        Level = LogLevel.Information,
        Message = "BEIR dataset '{DatasetName}' is already extracted at '{DatasetDirectory}'; nothing was downloaded")]
    internal static partial void DatasetAlreadyCached(ILogger logger, string datasetName, string datasetDirectory);

    [LoggerMessage(
        EventId = 557323883, EventName = "downloading_dataset",
        Level = LogLevel.Information,
        Message = "Downloading BEIR dataset '{DatasetName}' from '{ArchiveUrl}' into '{CacheDirectory}'; datasets are cached, never vendored into the repository")]
    internal static partial void DownloadingDataset(
        ILogger logger, string datasetName, Uri archiveUrl, string cacheDirectory);

    [LoggerMessage(
        EventId = 948131397, EventName = "archive_verified",
        Level = LogLevel.Information,
        Message = "Verified BEIR dataset '{DatasetName}': {ByteCount} bytes, MD5 {ArchiveMd5} matches the checksum BEIR publishes")]
    internal static partial void ArchiveVerified(ILogger logger, string datasetName, long byteCount, string archiveMd5);

    [LoggerMessage(
        EventId = 1537849576, EventName = "embedding_cache_batch",
        Level = LogLevel.Debug,
        Message = "Embedding cache for '{ModelIdentity}': {HitCount} served from disk, {MissCount} embedded; keys cover the model identity as well as the text, so a model change is a miss rather than a stale vector")]
    internal static partial void EmbeddingCacheBatch(
        ILogger logger, string modelIdentity, int hitCount, int missCount);
}
