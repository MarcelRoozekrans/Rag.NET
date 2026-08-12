namespace Rag.NET.Graph;

/// <summary>A community of related entities detected by <c>Leiden</c>.</summary>
public sealed record Community(
    int Id,
    int Level,
    IReadOnlyList<string> MemberEntities,
    string? ReportSummary);
