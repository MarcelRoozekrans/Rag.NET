namespace Rag.NET.DataProviders;

/// <summary>Summary of a completed <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> run.</summary>
public sealed record ProviderIngestionResult(
    int Ingested,
    int Skipped,
    int Deleted,
    IReadOnlyList<string> Errors);
