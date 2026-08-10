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
        if (options is not null && Validate(options) is { } invalid)
            return Result<IReadOnlyList<SearchResult>, RagError>.Failure(invalid);

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
    /// The generated validator for per-call options, with its nested
    /// <c>EnsembleOptionsValidator</c>; both are stateless, so one instance serves every call.
    /// This replaced a hand-written check block that early-returned on the first failure — the
    /// generated validator reports every failure at once, and covers the properties the manual
    /// block never remembered to (the candidate counts and <c>MinScore</c>, issue #94).
    /// </summary>
    private static readonly RetrievalOptionsValidator OptionsValidator =
        new RetrievalOptionsValidator(new EnsembleOptionsValidator());

    /// <summary>
    /// Rejects per-call options that violate the constraints declared on
    /// <see cref="RetrievalOptions"/> and <see cref="EnsembleOptions"/>, so a bad value fails
    /// loudly here instead of silently skewing a pipeline stage. Failures are mapped into
    /// <see cref="Models.ValidationFailure"/> the same way <c>PipelineIngestor.MapFailures</c>
    /// does: the span hoisted into a local, then an indexed loop into an array —
    /// <c>ValidationFailure</c> is a non-readonly struct, so enumerating the span by value
    /// trips EPS06 and indexing the property result directly trips HLQ013.
    /// </summary>
    private static RagError? Validate(RetrievalOptions options)
    {
        var result = OptionsValidator.Validate(options);
        if (result.IsValid)
            return null;

        var failures = result.Failures;
        var mapped = new Models.ValidationFailure[failures.Length];
        for (var i = 0; i < failures.Length; i++)
            mapped[i] = new Models.ValidationFailure(failures[i].PropertyName, failures[i].ErrorMessage);
        return new RagError.ValidationFailed(mapped);
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
