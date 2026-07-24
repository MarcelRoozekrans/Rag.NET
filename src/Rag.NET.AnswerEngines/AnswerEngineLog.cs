using Microsoft.Extensions.Logging;

namespace Rag.NET.AnswerEngines;

internal static partial class AnswerEngineLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Map-reduce map call failed for document '{DocumentId}', treating as not found")]
    internal static partial void MapReduceMapFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refine call failed for document '{DocumentId}', preserving previous answer")]
    internal static partial void RefineStepFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Confidence self-assessment returned an unparsable response; failing open with score 1.0")]
    internal static partial void ConfidenceScoreUnparsable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Confidence self-assessment LLM call failed; failing open with score 1.0")]
    internal static partial void ConfidenceScoreFailedOpen(ILogger logger, Exception exception);
}
