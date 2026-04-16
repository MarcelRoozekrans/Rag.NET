namespace Rag.NET.Security;

/// <summary>Records an answer generation operation for audit purposes.</summary>
public sealed record AuditAnswerEvent
{
    public required string RequestId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>The generated answer text. Only populated when <see cref="AuditLogOptions.LogAnswerText"/> is <see langword="true"/>.</summary>
    public string? Answer { get; init; }
}
