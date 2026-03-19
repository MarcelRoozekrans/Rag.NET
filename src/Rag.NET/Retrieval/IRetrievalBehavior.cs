using Rag.NET.Models;

namespace Rag.NET.Retrieval;

public interface IRetrievalBehavior
{
    ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx,
        CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next);
}
