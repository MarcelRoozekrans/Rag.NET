namespace Rag.NET.Security;

/// <summary>
/// Structured audit trail of retrieval and answer-generation operations.
/// Implementations must never throw — errors should be logged internally.
/// </summary>
public interface IAuditLog
{
    ValueTask LogRetrievalAsync(AuditRetrievalEvent ev, CancellationToken ct = default);
    ValueTask LogAnswerAsync(AuditAnswerEvent ev, CancellationToken ct = default);
}
