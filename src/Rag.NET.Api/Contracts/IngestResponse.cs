namespace Rag.NET.Api.Contracts;

public sealed record IngestResponse(string DocumentId, int ChunksStored);
