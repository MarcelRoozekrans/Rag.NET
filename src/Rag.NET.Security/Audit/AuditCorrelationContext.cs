namespace Rag.NET.Security;

/// <summary>
/// Flows the audit <c>RequestId</c> from <see cref="AuditRetrievalBehavior"/> to
/// <see cref="AuditAnswerEngineDecorator"/> via <see cref="AsyncLocal{T}"/>.
/// Register as a singleton — the AsyncLocal handles per-request scoping.
/// </summary>
public sealed class AuditCorrelationContext
{
    private readonly AsyncLocal<string?> _requestId = new();

    /// <summary>The current request ID, set by <see cref="AuditRetrievalBehavior"/> after retrieval.</summary>
    public string? RequestId
    {
        get => _requestId.Value;
        set => _requestId.Value = value;
    }
}
