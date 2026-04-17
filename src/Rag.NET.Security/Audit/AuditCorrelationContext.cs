namespace Rag.NET.Security;

/// <summary>
/// Carries the audit <c>RequestId</c> from <see cref="AuditRetrievalBehavior"/> to
/// <see cref="AuditAnswerEngineDecorator"/> within a single logical request.
/// <para>
/// Retrieval and answer generation are sequential: <see cref="AuditRetrievalBehavior"/> writes
/// the ID and <see cref="AuditAnswerEngineDecorator"/> reads it in the same continuation chain,
/// so a plain field is correct. For concurrent-request scenarios register as scoped instead of
/// singleton so each request gets its own instance.
/// </para>
/// </summary>
public sealed class AuditCorrelationContext
{
    /// <summary>The current request ID, set by <see cref="AuditRetrievalBehavior"/> after retrieval.</summary>
    public string? RequestId { get; set; }
}
