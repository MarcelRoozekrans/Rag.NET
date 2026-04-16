using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;

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

    public string Sanitise(string text, IReadOnlyDictionary<string, string> metadata)
    {
        if (text is null) return string.Empty;
        var fileName = metadata.TryGetValue("file_name", out var fn) ? fn : "<unknown>";
        try
        {
            var prompt = PiiPromptTemplate.Replace("{text}", text, StringComparison.Ordinal);
            var response = chatClient
                .GetResponseAsync([new ChatMessage(ChatRole.User, prompt)])
                .ConfigureAwait(false).GetAwaiter().GetResult();
            var result = response.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(result))
            {
                LogLlmPiiRedacted(_logger, fileName);
                return result;
            }
            return text;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogLlmFailed(_logger, ex);
            return _fallback.Sanitise(text, metadata);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "LLM PII sanitiser redacted content in chunk from '{FileName}'.")]
    private static partial void LogLlmPiiRedacted(ILogger logger, string fileName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "LLM PII sanitiser failed; falling back to regex sanitiser.")]
    private static partial void LogLlmFailed(ILogger logger, Exception ex);
}
