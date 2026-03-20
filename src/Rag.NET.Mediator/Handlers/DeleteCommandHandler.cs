using Rag.NET.Abstractions;
using Rag.NET.Mediator.Requests;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Handlers;

public sealed class DeleteCommandHandler(IIngestor ingestor)
    : IRequestHandler<DeleteCommand, Result<Unit, RagError>>
{
    // Parameterless constructor for ZeroAlloc.Mediator generator fallback.
    // In practice always resolved via DI factory.
    public DeleteCommandHandler() : this(null!) { }

    public async ValueTask<Result<Unit, RagError>> Handle(DeleteCommand request, CancellationToken ct)
    {
        await ingestor.DeleteAsync(request.DocumentId, ct).ConfigureAwait(false);
        return Result<Unit, RagError>.Success(Unit.Value);
    }
}
