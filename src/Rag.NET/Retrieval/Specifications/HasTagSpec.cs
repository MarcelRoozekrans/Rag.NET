using System.Linq.Expressions;
using Rag.NET.Models;
using ZeroAlloc.Specification;

namespace Rag.NET.Retrieval.Specifications;

[Specification]
public readonly partial struct HasTagSpec(string key, string value) : ISpecification<SearchResult>
{
    public bool IsSatisfiedBy(SearchResult r) =>
        r.Chunk.Metadata.TryGetValue(key, out var v) &&
        string.Equals(v, value, StringComparison.Ordinal);

    public Expression<Func<SearchResult, bool>> ToExpression()
    {
        var capturedKey = key;
        var capturedValue = value;
        // Expression tree uses == operator (not string.Equals with StringComparison) because
        // expression tree consumers (IQueryable, ORM translators) cannot translate the Ordinal overload.
        // Behavior is identical for in-memory use; == on string is ordinal under the hood in .NET.
        return r => r.Chunk.Metadata.ContainsKey(capturedKey) &&
                    r.Chunk.Metadata[capturedKey] == capturedValue;
    }
}
