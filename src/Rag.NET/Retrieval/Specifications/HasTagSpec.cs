using System.Linq.Expressions;
using Rag.NET.Models;
using ZeroAlloc.Specification;

namespace Rag.NET.Retrieval.Specifications;

[Specification]
public readonly partial struct HasTagSpec(string key, string value) : ISpecification<SearchResult>
{
    public bool IsSatisfiedBy(SearchResult r) =>
        r.Chunk.Metadata.TryGetValue(key, out var v) &&
        v == value;

    public Expression<Func<SearchResult, bool>> ToExpression()
    {
        var capturedKey = key;
        var capturedValue = value;
        // MetadataValue's == is typed value equality (ordinal for strings); the tag value here
        // is a string, so only a String-kind metadata value can match it.
        return r => r.Chunk.Metadata.ContainsKey(capturedKey) &&
                    r.Chunk.Metadata[capturedKey] == capturedValue;
    }
}
