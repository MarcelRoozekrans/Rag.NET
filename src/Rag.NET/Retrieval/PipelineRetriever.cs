using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Telemetry;
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
        if (options is not null)
        {
            if (options.TopK <= 0)
                return Result<IReadOnlyList<SearchResult>, RagError>.Failure(
                    new RagError.ValidationFailed([new Models.ValidationFailure("TopK", "TopK must be greater than 0.")]));
            if (options.RedundancyThreshold < 0.0f || options.RedundancyThreshold > 1.0f)
                return Result<IReadOnlyList<SearchResult>, RagError>.Failure(
                    new RagError.ValidationFailed([new Models.ValidationFailure("RedundancyThreshold", "RedundancyThreshold must be between 0.0 and 1.0.")]));
            if (options.MmrLambda < 0.0f || options.MmrLambda > 1.0f)
                return Result<IReadOnlyList<SearchResult>, RagError>.Failure(
                    new RagError.ValidationFailed([new Models.ValidationFailure("MmrLambda", "MmrLambda must be between 0.0 and 1.0.")]));
        }

        var resolvedOptions = options ?? new RetrievalOptions();
        var ctx = new RetrievalContext
        {
            Query = query,
            Options = resolvedOptions,
            Logger = (ILogger?)Logger ?? NullLogger.Instance,
        };

        var queryHash = HashQuery(query);

        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.retrieve");
        activity?.SetTag("query.hash", queryHash);
        activity?.SetTag("top.k", resolvedOptions.TopK);

        using var scope = Logger?.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal) { ["query_hash"] = queryHash });

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await Pipeline.ExecuteAsync(ctx, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("result.count", result.Count);
            RagTelemetry.ChunksRetrieved.Add(result.Count);
            return Result<IReadOnlyList<SearchResult>, RagError>.Success(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RagTelemetry.RetrieveErrors.Add(1);
            return Result<IReadOnlyList<SearchResult>, RagError>.Failure(new RagError.StorageFailed(ex));
        }
        finally
        {
            sw.Stop();
            RagTelemetry.RetrieveDuration.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// SHA-256 8-char query hash used for both the <c>query.hash</c> span tag and the
    /// <c>query_hash</c> log scope. Internal so <see cref="Pipeline.RagPipeline"/> can reuse the
    /// same hash for its answering scope rather than computing a second one — and so the raw
    /// query text, which is PII, never has to leave this method.
    /// </summary>
    internal static string HashQuery(string query)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(query));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
