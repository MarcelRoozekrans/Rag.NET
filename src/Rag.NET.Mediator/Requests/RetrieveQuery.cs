using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Requests;

public sealed record RetrieveQuery(string Query, RetrievalOptions? Options = null)
    : IRequest<Result<IReadOnlyList<SearchResult>, RagError>>;
