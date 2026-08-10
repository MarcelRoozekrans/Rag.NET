using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Telemetry;

namespace Rag.NET.Security;

/// <summary>
/// Detects and redacts PII from chunk text using an LLM call.
/// Runs after <see cref="PiiChunkSanitiser"/> when both are registered.
/// Falls back to <see cref="PiiChunkSanitiser"/> (default options) on LLM failure.
/// Never throws — returns original (or regex-sanitised) text on failure.
/// </summary>
public sealed partial class LlmPiiChunkSanitiser(
    IChatClient chatClient,
    ILogger<LlmPiiChunkSanitiser>? logger = null) : IChunkSanitiser
{
    private readonly ILogger<LlmPiiChunkSanitiser> _logger =
        logger ?? NullLogger<LlmPiiChunkSanitiser>.Instance;
    private readonly PiiChunkSanitiser _fallback =
        new(new PiiDetectionOptions(), NullLogger<PiiChunkSanitiser>.Instance);

    private const string PiiPromptTemplate =
        "Return the following text with all personally identifiable information (PII) replaced " +
        "by typed placeholders such as [EMAIL], [PHONE], [SSN], [CREDIT_CARD], [IP_ADDRESS], [NAME]. " +
        "Return only the modified text with no explanation.\n\nText:\n{text}";

    public string Sanitise(string text, IReadOnlyDictionary<string, MetadataValue> metadata)
    {
        if (text is null) return string.Empty;

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.security.sanitize");
        activity?.SetTag("security.sanitizer.type", "llm-pii-chunk");

        var fileName = metadata.TryGetValue(ReservedMetadataKeys.FileName, out var fn) ? fn.ToString() : "<unknown>";
        try
        {
            var prompt = PiiPromptTemplate.Replace("{text}", text, StringComparison.Ordinal);
            var response = chatClient
                .GetResponseAsync([new ChatMessage(ChatRole.User, prompt)])
                .ConfigureAwait(false).GetAwaiter().GetResult();
            var result = response.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(result))
            {
                var changed = !string.Equals(result, text, StringComparison.Ordinal);
                if (changed)
                    LogLlmPiiRedacted(_logger, fileName);
                activity?.SetTag("security.matches.count", changed ? 1 : 0);
                return result;
            }
            LogLlmEmptyResponse(_logger, fileName);
            return _fallback.Sanitise(text, metadata);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogLlmFailed(_logger, ex);
            return _fallback.Sanitise(text, metadata);
        }
    }

    [LoggerMessage(EventId = 2025494598, EventName = "log_llm_pii_redacted", Level = LogLevel.Information,
        Message = "LLM PII sanitiser redacted content in chunk from '{FileName}'.")]
    private static partial void LogLlmPiiRedacted(ILogger logger, string fileName);

    [LoggerMessage(EventId = 205407954, EventName = "log_llm_empty_response", Level = LogLevel.Warning,
        Message = "LLM PII sanitiser returned empty response for '{FileName}'; falling back to regex sanitiser.")]
    private static partial void LogLlmEmptyResponse(ILogger logger, string fileName);

    [LoggerMessage(EventId = 1385825225, EventName = "log_llm_failed", Level = LogLevel.Warning,
        Message = "LLM PII sanitiser failed; falling back to regex sanitiser.")]
    private static partial void LogLlmFailed(ILogger logger, Exception ex);
}
