namespace Rag.NET.Security;

/// <summary>No-op <see cref="IAuditLog"/> used when audit logging is not configured.</summary>
public sealed class NoOpAuditLog : IAuditLog
{
    public static readonly NoOpAuditLog Instance = new();
    public ValueTask LogRetrievalAsync(AuditRetrievalEvent ev, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask LogAnswerAsync(AuditAnswerEvent ev, CancellationToken ct = default) => ValueTask.CompletedTask;
}
