using System.Linq.Expressions;
using Rag.NET.Models;
using ZeroAlloc.Specification;

namespace Rag.NET.Retrieval.Specifications;

[Specification]
public readonly partial struct DocumentIdSpec(DocumentId id) : ISpecification<SearchResult>
{
    public bool IsSatisfiedBy(SearchResult r) => r.Chunk.DocumentId == id;

    public Expression<Func<SearchResult, bool>> ToExpression()
    {
        var capturedId = id;
        return r => r.Chunk.DocumentId == capturedId;
    }
}
