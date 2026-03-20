using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Requests;

public sealed record IngestCommand(
    Stream Content,
    DocumentMetadata Metadata,
    IngestionOptions? Options = null,
    IProgress<IngestionProgress>? Progress = null)
    : IRequest<Result<IngestionResult, RagError>>;
