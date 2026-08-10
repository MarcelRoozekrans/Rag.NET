using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Telemetry;

namespace Rag.NET.Security;

/// <summary>
/// Filters retrieved chunks to those whose <c>allowed_roles</c> metadata intersects the caller's roles.
/// Chunks with no <c>allowed_roles</c> metadata are world-readable and always pass through.
/// Pass-through (no allocation) when all chunks are public or none are filtered.
/// </summary>
public sealed partial class RbacRetrievalGuard(
    ICallerContext callerContext,
    ILogger<RbacRetrievalGuard>? logger = null) : IRetrievalGuard
{
    private readonly ILogger<RbacRetrievalGuard> _logger =
        logger ?? NullLogger<RbacRetrievalGuard>.Instance;

    public IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.security.guard");
        activity?.SetTag("security.guard.type", "rbac");
        activity?.SetTag("security.guard.action", "drop");

        var callerRoles = callerContext.GetRoles();

        List<SearchResult>? filtered = null;
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (!result.Chunk.Metadata.TryGetValue(ReservedMetadataKeys.AllowedRoles, out var allowedRolesRaw))
            {
                filtered?.Add(result);
                continue;
            }

            var allowedRoles = allowedRolesRaw.ToString().Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hasAccess = false;
            foreach (var callerRole in callerRoles)
            {
                foreach (var allowedRole in allowedRoles)
                {
                    if (string.Equals(allowedRole, callerRole, StringComparison.OrdinalIgnoreCase))
                    {
                        hasAccess = true;
                        break;
                    }
                }
                if (hasAccess)
                    break;
            }

            if (!hasAccess)
            {
                LogAccessDenied(_logger, result.Chunk.DocumentId.Value);
                if (filtered is null)
                {
                    filtered = new List<SearchResult>(i);
                    for (var j = 0; j < i; j++)
                        filtered.Add(results[j]);
                }
                continue;
            }

            filtered?.Add(result);
        }
        activity?.SetTag("security.chunks.affected", results.Count - (filtered?.Count ?? results.Count));
        return filtered is not null ? filtered.AsReadOnly() : results;
    }

    [LoggerMessage(EventId = 277457468, EventName = "log_access_denied", Level = LogLevel.Warning,
        Message = "RBAC: chunk from '{DocumentId}' filtered — caller lacks required role.")]
    private static partial void LogAccessDenied(ILogger logger, string documentId);
}
