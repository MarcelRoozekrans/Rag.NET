using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval;

/// <summary>
/// Thin facade over the retrieval pipeline.
/// Replaces the nested decorator factory (BuildRetrieverChain).
/// </summary>
[Singleton(As = typeof(IRetriever))]
public sealed class PipelineRetriever : IRetriever
{
    [Inject] public Pipeline<RetrievalContext, IReadOnlyList<SearchResult>> Pipeline { get; set; } = null!;
    [Inject(Required = false)] public ILogger<PipelineRetriever>? Logger { get; set; }

    public Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = new RetrievalContext
        {
            Query = query,
            Options = options ?? new RetrievalOptions(),
            Logger = (ILogger?)Logger ?? NullLogger.Instance,
        };

        return Pipeline.ExecuteAsync(ctx, cancellationToken).AsTask();
    }
}
