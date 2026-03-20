using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;

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

    public async Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
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

        try
        {
            var result = await Pipeline.ExecuteAsync(ctx, cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyList<SearchResult>, RagError>.Success(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<SearchResult>, RagError>.Failure(new RagError.StorageFailed(ex));
        }
    }
}
