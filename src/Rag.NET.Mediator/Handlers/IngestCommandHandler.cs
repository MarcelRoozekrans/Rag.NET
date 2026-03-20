using Rag.NET.Abstractions;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Handlers;

public sealed class IngestCommandHandler(IIngestor ingestor)
    : IRequestHandler<IngestCommand, Result<IngestionResult, RagError>>
{
    // Parameterless constructor for ZeroAlloc.Mediator generator fallback.
    // In practice always resolved via DI factory.
    public IngestCommandHandler() : this(null!) { }

    public ValueTask<Result<IngestionResult, RagError>> Handle(IngestCommand request, CancellationToken ct)
        => new(ingestor.IngestAsync(request.Content, request.Metadata, request.Options, request.Progress, ct));
}
