using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Security;

public sealed class QuerySanitiserPipelineDecorator(
    IRagPipeline inner,
    IEnumerable<IQuerySanitiser> sanitisers) : IRagPipeline
{
    public Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => inner.IngestAsync(document, metadata, options, progress, cancellationToken);

    public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
        => inner.RetrieveAsync(query, options, cancellationToken);

    public Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
        => inner.AskAsync(SanitiseQuery(query), options, cancellationToken);

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in inner.AskStreamingAsync(SanitiseQuery(query), options, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
        => inner.DeleteAsync(documentId, cancellationToken);

    private string SanitiseQuery(string query)
    {
        foreach (var s in sanitisers)
            query = s.Sanitise(query);
        return query;
    }
}
