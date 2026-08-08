using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Internal;

/// <summary>
/// Adapts the generated, assembly-internal <c>IMediator</c> to the public
/// <see cref="IRagMediator"/> seam. Every member forwards to <paramref name="inner"/> unchanged.
/// </summary>
internal sealed class RagMediator(IMediator inner) : IRagMediator
{
    public ValueTask<Result<IngestionResult, RagError>> Send(IngestCommand request, CancellationToken ct = default) =>
        inner.Send(request, ct);

    public ValueTask<Result<IReadOnlyList<SearchResult>, RagError>> Send(RetrieveQuery request, CancellationToken ct = default) =>
        inner.Send(request, ct);

    public ValueTask<Result<Unit, RagError>> Send(DeleteCommand request, CancellationToken ct = default) =>
        inner.Send(request, ct);
}
