using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Security;

public sealed partial class TrustLevelRetrievalGuard(
    TrustLevelGuardOptions options,
    ILogger<TrustLevelRetrievalGuard>? logger = null) : IRetrievalGuard
{
    private readonly ILogger<TrustLevelRetrievalGuard> _logger =
        logger ?? NullLogger<TrustLevelRetrievalGuard>.Instance;

    public IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results)
    {
        List<SearchResult>? filtered = null;
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var trustLevel = result.Chunk.Metadata.TryGetValue("trust_level", out var tl) ? tl : "internal";
            var docId = result.Chunk.DocumentId.Value;

            if (string.Equals(trustLevel, "untrusted", StringComparison.OrdinalIgnoreCase) && options.DropUntrusted)
            {
                LogUntrustedDropped(_logger, docId);
                if (filtered is null)
                {
                    filtered = new List<SearchResult>(i);
                    for (var j = 0; j < i; j++)
                        filtered.Add(results[j]);
                }
                continue;
            }

            if (string.Equals(trustLevel, "external", StringComparison.OrdinalIgnoreCase) && options.WarnOnExternal)
                LogExternalWarning(_logger, docId);

            filtered?.Add(result);
        }
        return filtered is not null ? filtered.AsReadOnly() : results;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Dropping chunk from '{DocumentId}' — trust_level=untrusted.")]
    private static partial void LogUntrustedDropped(ILogger logger, string documentId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Retrieved chunk from '{DocumentId}' has trust_level=external — treat with caution.")]
    private static partial void LogExternalWarning(ILogger logger, string documentId);
}
