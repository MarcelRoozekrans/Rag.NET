using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Security;

public sealed partial class RegexRetrievalGuard(
    ILogger<RegexRetrievalGuard>? logger = null) : IRetrievalGuard
{
    private readonly ILogger<RegexRetrievalGuard> _logger =
        logger ?? NullLogger<RegexRetrievalGuard>.Instance;

    public IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results)
    {
        List<SearchResult>? modified = null;
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            try
            {
                var docId = result.Chunk.DocumentId.Value;
                var sanitised = InjectionPatterns.InjectionPattern().Replace(result.Chunk.Text, m =>
                {
                    LogInjectionDetected(_logger, docId, m.Value);
                    return "[REDACTED]";
                });

                if (!ReferenceEquals(sanitised, result.Chunk.Text))
                {
                    modified ??= new List<SearchResult>(results);
                    modified[i] = result with { Chunk = result.Chunk with { Text = sanitised } };
                }
            }
            // Non-blocking by design: failures return the chunk unmodified. OperationCanceledException propagates.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogInspectFailed(_logger, ex);
            }
        }
        return modified is not null ? modified.AsReadOnly() : results;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Prompt injection pattern detected in retrieved chunk from '{DocumentId}': matched '{Pattern}'.")]
    private static partial void LogInjectionDetected(ILogger logger, string documentId, string pattern);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "RegexRetrievalGuard failed on a chunk; chunk returned unmodified.")]
    private static partial void LogInspectFailed(ILogger logger, Exception ex);
}
