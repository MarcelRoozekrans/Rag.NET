using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Pipeline;

/// <summary>
/// Thin coordinator that delegates to <see cref="IRetriever"/>, <see cref="IIngestor"/>,
/// and <see cref="IAnswerEngine"/>. The public <see cref="IRagPipeline"/> facade is unchanged.
/// </summary>
public sealed class RagPipeline(
    IRetriever retriever,
    IIngestor ingestor,
    IAnswerEngine? answerEngine = null) : IRagPipeline
{
    public Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ingestor.IngestAsync(document, metadata, options, progress, cancellationToken);

    public Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
        => retriever.RetrieveAsync(query, options, cancellationToken);

    public async Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (answerEngine is null)
            throw new InvalidOperationException(
                "IAnswerEngine is not registered. Register an IChatClient in DI to use AskAsync.");

        var opts = options ?? new RagOptions();
        var retrievalOptions = new RetrievalOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
            UseLostInTheMiddleReordering = opts.UseLostInTheMiddleReordering,
            UseRedundancyFilter = opts.UseRedundancyFilter,
            RedundancyThreshold = opts.RedundancyThreshold,
        };
        var sources = await retriever.RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);

        return await answerEngine.AskAsync(query, sources, opts, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (answerEngine is null)
            throw new InvalidOperationException(
                "IAnswerEngine is not registered. Register an IChatClient in DI to use AskStreamingAsync.");

        var opts = options ?? new RagOptions();
        var retrievalOptions = new RetrievalOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
            UseLostInTheMiddleReordering = opts.UseLostInTheMiddleReordering,
            UseRedundancyFilter = opts.UseRedundancyFilter,
            RedundancyThreshold = opts.RedundancyThreshold,
        };
        var sources = await retriever.RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);

        await foreach (var update in answerEngine.AskStreamingAsync(query, sources, opts, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
        => ingestor.DeleteAsync(documentId, cancellationToken);
}
