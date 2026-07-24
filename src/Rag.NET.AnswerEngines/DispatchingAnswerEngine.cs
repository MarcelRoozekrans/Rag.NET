using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Routes answer generation to the appropriate engine based on <see cref="RagOptions.SynthesisStrategy"/>.
/// </summary>
/// <remarks>
/// The FLARE engine is optional — it requires <c>UseFlare()</c> registration (retriever +
/// confidence scorer). Requesting <see cref="SynthesisStrategy.Flare"/> without it throws.
/// Note: unlike the Chat/MapReduce/Refine engines, FLARE does not consult
/// <c>IConversationMemory</c> — routing a call to Flare drops the processed conversation
/// history from the prompt.
/// </remarks>
public sealed class DispatchingAnswerEngine(
    IAnswerEngine chatEngine,
    IAnswerEngine mapReduceEngine,
    IAnswerEngine refineEngine,
    IAnswerEngine? flareEngine = null) : IAnswerEngine
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
            SynthesisStrategy.Flare     => flareEngine ?? throw new InvalidOperationException(
                $"{nameof(SynthesisStrategy)}.{nameof(SynthesisStrategy.Flare)} requested but no FLARE engine is registered. " +
                "Call UseFlare() before UseDispatchingAnswerEngine()."),
            _                           => chatEngine,
        };
}
