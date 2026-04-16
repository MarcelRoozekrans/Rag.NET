namespace Rag.NET.Security;

/// <summary>Records a retrieval operation for audit purposes.</summary>
public sealed record AuditRetrievalEvent
{
    /// <summary>Correlates this retrieval event with the corresponding <see cref="AuditAnswerEvent"/>.</summary>
    public required string RequestId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required IReadOnlyList<string> CallerRoles { get; init; }
    public required IReadOnlyList<AuditChunkRef> Chunks { get; init; }
    /// <summary>The raw query string. Only populated when <see cref="AuditLogOptions.LogQueryText"/> is <see langword="true"/>.</summary>
    public string? Query { get; init; }
}
