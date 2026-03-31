using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Generates answers from pre-retrieved search results using an LLM.
/// Implementations may compose as decorators to alter generation strategy.
/// </summary>
public interface IAnswerEngine
{
    Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);
}
