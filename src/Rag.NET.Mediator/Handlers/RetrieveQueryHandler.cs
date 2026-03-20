using Rag.NET.Abstractions;
using Rag.NET.Mediator.Requests;
using Rag.NET.Models;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Handlers;

public sealed class RetrieveQueryHandler(IRetriever retriever)
    : IRequestHandler<RetrieveQuery, Result<IReadOnlyList<SearchResult>, RagError>>
{
    // Parameterless constructor for ZeroAlloc.Mediator generator fallback.
    // In practice always resolved via DI factory.
    public RetrieveQueryHandler() : this(null!) { }

    public ValueTask<Result<IReadOnlyList<SearchResult>, RagError>> Handle(RetrieveQuery request, CancellationToken ct)
        => new(retriever.RetrieveAsync(request.Query, request.Options, ct));
}
