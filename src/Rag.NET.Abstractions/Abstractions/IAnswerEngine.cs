using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Generates answers from pre-retrieved search results using an LLM.
/// Implementations may compose as decorators to alter generation strategy.
/// </summary>
public interface IAnswerEngine
{
    /// <summary>
    /// Generates a complete answer in one call. Always makes at least one LLM call; strategies
    /// like MapReduce and Refine make one per source chunk in addition to any combination step.
    /// </summary>
    Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an answer incrementally, yielding text as the LLM produces it rather than
    /// waiting for the complete response. Same LLM call volume as <see cref="AskAsync"/>.
    /// </summary>
    IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default);
}
