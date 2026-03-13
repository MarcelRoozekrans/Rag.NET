namespace Rag.NET.Api.Contracts;

public sealed record SearchResultDto(
    string Text,
    string DocumentId,
    int ChunkIndex,
    double Score,
    IReadOnlyDictionary<string, string> Metadata);
