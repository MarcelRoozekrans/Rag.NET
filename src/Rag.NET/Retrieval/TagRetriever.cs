using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Retrieval;

/// <summary>
/// <see cref="IRetriever"/> decorator that automatically injects <c>MetadataFilter</c> entries
/// derived from semantic tag matching. Tag embeddings are populated during ingestion by
/// <see cref="Rag.NET.Ingestion.Behaviors.TagIngestionBehavior"/>.
/// </summary>
public sealed class TagRetriever : IRetriever
{
    private readonly IRetriever _inner;
    private readonly ITagIndex _tagIndex;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly TagRetrievalOptions _options;
    private readonly ILogger<TagRetriever>? _logger;

    public TagRetriever(
        IRetriever inner,
        ITagIndex tagIndex,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        TagRetrievalOptions options,
        ILogger<TagRetriever>? logger = null)
    {
        _inner   = inner;
        _tagIndex = tagIndex;
        _embedder = embedder;
        _options  = options;
        _logger   = logger;
    }

    public async Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? new RetrievalOptions();

        if (!effective.UseTagRetrieval)
            return await _inner.RetrieveAsync(query, effective, cancellationToken).ConfigureAwait(false);

        var merged = await TryInjectTagFilterAsync(query, effective, cancellationToken).ConfigureAwait(false);
        return await _inner.RetrieveAsync(query, merged, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetrievalOptions> TryInjectTagFilterAsync(
        string query, RetrievalOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var embeddings = await _embedder
                .GenerateAsync([query], cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var matches = _tagIndex.Search(embeddings[0].Vector, _options.MinScore);
            if (matches.Count == 0)
                return options;

            // Take at most one match per key (highest score — index returns score-desc)
            var injected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value, _) in matches)
            {
                if (!injected.ContainsKey(key) && injected.Count < _options.TopK)
                    injected[key] = value;
            }

            // Merge into caller's existing MetadataFilter — caller's entries win (TryAdd)
            var filter = options.MetadataFilter is not null
                ? new Dictionary<string, MetadataValue>(options.MetadataFilter, StringComparer.Ordinal)
                : new Dictionary<string, MetadataValue>(StringComparer.Ordinal);

            foreach (var (key, value) in injected)
                filter.TryAdd(key, value);

            return options with { MetadataFilter = filter };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Tag filter injection failed; proceeding without tag filter");
            return options;
        }
    }
}
