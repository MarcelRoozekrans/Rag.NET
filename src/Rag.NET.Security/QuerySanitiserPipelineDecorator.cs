using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Security;

/// <summary>
/// Strips known prompt-injection patterns from a query before the model sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Covers the two paths that reach a model, and deliberately not the third.</b>
/// <c>AskAsync</c> and <c>AskStreamingAsync</c> sanitise; <c>RetrieveAsync</c> forwards the query
/// unchanged. Prompt injection hijacks a model, and retrieval reaches none — it returns chunks to
/// the caller, who decides what to do with them. Redacting <c>act as</c> or
/// <c>ignore previous</c> from a legitimate query <i>about</i> those phrases would corrupt the
/// search terms while protecting nothing.
/// </para>
/// <para>
/// <b>The cost, stated rather than left to be discovered.</b> A retrieval-only caller — fetching
/// chunks and generating elsewhere — registers the sanitiser and gets nothing on their path. The
/// defence is real but narrower than its name suggests, which is why
/// <c>RagBuilderExtensions.UseQuerySanitiser</c> names the scope in its own remarks.
/// </para>
/// <para>
/// <b>Why this is written down at all.</b>
/// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/559">#559</see> asked whether the
/// omission was deliberate, and nothing could answer: no doc comment on this file, no test over
/// that path, no published page mentioning it. The behaviour was defensible and unrecorded, which
/// is indistinguishable from an oversight to everyone except its author. Confirmed deliberate by
/// the maintainer on 2026-09-13 and pinned by
/// <c>QuerySanitiserPipelineDecoratorTests.RetrieveAsync_ForwardsTheQueryUnsanitised</c>.
/// </para>
/// <para>
/// <b>Fails open.</b> A sanitiser that throws returns the original query and logs, so a broken
/// sanitiser degrades the defence rather than the pipeline.
/// </para>
/// </remarks>
/// <param name="inner">The pipeline being decorated.</param>
/// <param name="sanitisers">The sanitisers to apply, in registration order.</param>
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

    /// <summary>Retrieves chunks, forwarding the query <b>unsanitised</b>.</summary>
    /// <param name="query">The caller's query, passed through as written.</param>
    /// <param name="options">Retrieval options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The inner pipeline's result.</returns>
    /// <remarks>
    /// Deliberate; see this type's remarks. Changing it changes what a documented defence covers,
    /// so revisit the decision rather than the assertion that pins it.
    /// </remarks>
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
