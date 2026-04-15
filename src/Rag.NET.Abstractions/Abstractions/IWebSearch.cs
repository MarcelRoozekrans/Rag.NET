using Rag.NET.Models;

namespace Rag.NET.Abstractions;

public interface IWebSearch
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken ct);
}
