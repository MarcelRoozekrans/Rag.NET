using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;
using Rag.NET.Retrieval;

namespace Rag.NET.Security;

/// <summary>
/// Retrieval pipeline behavior that writes a structured <see cref="AuditRetrievalEvent"/> after
/// each retrieval. Stores the <c>RequestId</c> in <c>ctx.Extensions["audit_request_id"]</c>
/// so <see cref="AuditAnswerEngineDecorator"/> can correlate retrieval and answer events.
/// Errors from <see cref="IAuditLog"/> are swallowed and logged — results are always returned.
/// </summary>
public sealed partial class AuditRetrievalBehavior(
    IAuditLog auditLog,
    ICallerContext callerContext,
    AuditLogOptions options,
    ILogger<AuditRetrievalBehavior>? logger = null) : IRetrievalBehavior
{
    private readonly ILogger<AuditRetrievalBehavior> _logger =
        logger ?? NullLogger<AuditRetrievalBehavior>.Instance;

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        var requestId = Guid.NewGuid().ToString("N");
        ctx.Extensions["audit_request_id"] = requestId;

        var chunks = new List<AuditChunkRef>(results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            chunks.Add(new AuditChunkRef
            {
                DocumentId = r.Chunk.DocumentId.Value,
                ChunkIndex = r.Chunk.ChunkIndex,
                Score      = r.Score,
            });
        }

        var ev = new AuditRetrievalEvent
        {
            RequestId   = requestId,
            Timestamp   = DateTimeOffset.UtcNow,
            CallerRoles = callerContext.GetRoles(),
            Chunks      = chunks.AsReadOnly(),
            Query       = options.LogQueryText ? ctx.Query : null,
        };

        try
        {
            await auditLog.LogRetrievalAsync(ev, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogAuditFailed(_logger, ex);
        }

        return results;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AuditRetrievalBehavior failed to write audit log entry.")]
    private static partial void LogAuditFailed(ILogger logger, Exception ex);
}
