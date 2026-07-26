using Microsoft.Extensions.Logging;

namespace Rag.NET.Memory;

internal static partial class ConversationMemoryLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Vector store '{StoreType}' reports an opaque ranking score scale, so its scores cannot be thresholded; " +
                  "PersistentMemoryOptions.MinScore ({MinScore}) is ignored and recall takes the top {TopK} matches by rank. " +
                  "Logged once per memory instance.")]
    internal static partial void OpaqueScoreScaleIgnoresMinScore(
        ILogger logger, string storeType, double minScore, int topK);
}
