using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Routes answer generation to the appropriate engine based on <see cref="RagOptions.SynthesisStrategy"/>.
/// </summary>
public sealed class DispatchingAnswerEngine(
    IAnswerEngine chatEngine,
    IAnswerEngine mapReduceEngine,
    IAnswerEngine refineEngine) : IAnswerEngine
{
    public Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var engine = Select(options);
        return engine.AskAsync(query, sources, options, cancellationToken);
    }

    public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var engine = Select(options);
        return engine.AskStreamingAsync(query, sources, options, cancellationToken);
    }

    private IAnswerEngine Select(RagOptions? options) =>
        (options?.SynthesisStrategy ?? SynthesisStrategy.Default) switch
        {
            SynthesisStrategy.MapReduce => mapReduceEngine,
            SynthesisStrategy.Refine    => refineEngine,
            _                           => chatEngine,
        };
}
