using System.Linq.Expressions;
using Rag.NET.Models;
using ZeroAlloc.Specification;

namespace Rag.NET.Retrieval.Specifications;

[Specification]
public readonly partial struct MinScoreSpec(double threshold) : ISpecification<SearchResult>
{
    public bool IsSatisfiedBy(SearchResult r) => r.Score >= threshold;

    public Expression<Func<SearchResult, bool>> ToExpression()
    {
        var captured = threshold;
        return r => r.Score >= captured;
    }
}
