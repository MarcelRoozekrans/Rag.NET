using Microsoft.Extensions.Logging;

namespace Rag.NET.AnswerEngines;

internal static partial class AnswerEngineLog
{
    [LoggerMessage(EventId = 1318312786, EventName = "map_reduce_map_failed", Level = LogLevel.Warning, Message = "Map-reduce map call failed for document '{DocumentId}', treating as not found")]
    internal static partial void MapReduceMapFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(EventId = 1606707285, EventName = "refine_step_failed", Level = LogLevel.Warning, Message = "Refine call failed for document '{DocumentId}', preserving previous answer")]
    internal static partial void RefineStepFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(EventId = 1420514382, EventName = "confidence_score_unparsable", Level = LogLevel.Warning, Message = "Confidence self-assessment returned an unparsable response; failing open with score 1.0")]
    internal static partial void ConfidenceScoreUnparsable(ILogger logger);

    [LoggerMessage(EventId = 53339104, EventName = "confidence_score_failed_open", Level = LogLevel.Warning, Message = "Confidence self-assessment LLM call failed; failing open with score 1.0")]
    internal static partial void ConfidenceScoreFailedOpen(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1803280340, EventName = "flare_lookahead_failed", Level = LogLevel.Warning, Message = "FLARE lookahead retrieval failed ({ErrorType}); keeping sentence and continuing with existing context")]
    internal static partial void FlareLookaheadFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 1344723153, EventName = "flare_lookahead_threw", Level = LogLevel.Warning, Message = "FLARE lookahead retrieval threw; keeping sentence and continuing with existing context")]
    internal static partial void FlareLookaheadThrew(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 912418838, EventName = "flare_lookahead_empty", Level = LogLevel.Debug, Message = "FLARE lookahead retrieval returned no results; keeping sentence without regeneration")]
    internal static partial void FlareLookaheadEmpty(ILogger logger);

    [LoggerMessage(EventId = 1212915965, EventName = "flare_scorer_threw", Level = LogLevel.Warning, Message = "Confidence scorer threw; treating sentence as fully confident (1.0)")]
    internal static partial void FlareScorerThrew(ILogger logger, Exception exception);
}
