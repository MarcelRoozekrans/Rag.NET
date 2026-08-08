using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator;

/// <summary>
/// Rag.NET's public dispatch surface for this package. ZeroAlloc.Mediator.Generator 5.x emits
/// its generated <c>IMediator</c> as <see langword="internal"/>: each assembly's copy is a
/// distinct type with different members, so it can never appear in a public signature across
/// an assembly boundary — that was a coincidence 4.x let compile, not a guarantee. Consumers
/// outside this assembly (Rag.NET.Api and friends) depend on <see cref="IRagMediator"/> instead.
/// It mirrors the generated <c>IMediator</c> one overload per request type this package actually
/// handles today and adds no behavior of its own — a thin adapter, not a new abstraction layer.
/// </summary>
public interface IRagMediator
{
    /// <summary>Dispatches an <see cref="IngestCommand"/> to its registered handler.</summary>
    ValueTask<Result<IngestionResult, RagError>> Send(IngestCommand request, CancellationToken ct = default);

    /// <summary>Dispatches a <see cref="RetrieveQuery"/> to its registered handler.</summary>
    ValueTask<Result<IReadOnlyList<SearchResult>, RagError>> Send(RetrieveQuery request, CancellationToken ct = default);

    /// <summary>Dispatches a <see cref="DeleteCommand"/> to its registered handler.</summary>
    ValueTask<Result<Unit, RagError>> Send(DeleteCommand request, CancellationToken ct = default);
}
