using Rag.NET.Abstractions;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Dense retrieval restricted by a metadata tag, run against a store holding <b>two</b> corpora.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two corpora, and why this row is not like the others.</b> Every other ablation row varies
/// the ranker over one corpus. This one leaves the ranker alone and changes what is in the store:
/// SciFact and FiQA are indexed together, every chunk tagged with the corpus it came from, and the
/// row filters back down to one of them. The question is not "does tag filtering score well" — it is
/// <b>does the filter restore exactly what a single-corpus store would have returned</b>.
/// </para>
/// <para>
/// <b>That makes the cell falsifiable, which the entry's original framing was not.</b> The allowlist
/// asked for "a filtered parity leg", and BEIR chunks carry no tags — so any tag vocabulary would
/// have been invented by whoever wrote the cell, and its figure would have measured the invention.
/// The corpus a document came from is not invented: it is a fact about the data, and it gives the
/// run a target it must hit rather than a number to report. The filtered figure must equal SciFact's
/// standalone Real dense figure, <c>0.67742</c>, to five decimals. Anything else is a leak.
/// </para>
/// <para>
/// <b>Its evidence that the mechanism ran</b> is <see cref="AssertEveryHitCarriedTheTag"/>: no chunk
/// from the other corpus may appear in any ranking. That is a stronger check than the figure alone,
/// because a filter could leak on queries whose leaked documents happen not to be judged, moving no
/// metric while still being wrong. The count is kept per hit rather than per query for the same
/// reason — one leaked chunk in one ranking fails it.
/// </para>
/// <para>
/// <b>What this row does NOT establish.</b> <c>InMemoryVectorStore</c> filters before scoring, so a
/// pass here says the in-memory path pre-filters correctly. It says nothing about stores that filter
/// after ranking, and nothing about the BM25 arm of client-side hybrid — see issue #350, which is a
/// separate leak on a path this row does not take.
/// </para>
/// </remarks>
public sealed class TagFilteredAblationRow : AblationRow
{
    /// <summary>The metadata key every unit carries, naming the corpus it was chunked from.</summary>
    public const string TagKey = "corpus";

    private readonly string _keep;
    private readonly int _corpusCount;

    /// <summary>Creates a row that keeps only units tagged with <paramref name="keep"/>.</summary>
    /// <param name="keep">The corpus tag to retain.</param>
    /// <param name="corpusCount">How many corpora the store holds, for the row's label.</param>
    public TagFilteredAblationRow(string keep, int corpusCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keep);

        _keep = keep;
        _corpusCount = corpusCount;
    }

    /// <summary>Gets how many queries this row has retrieved for.</summary>
    public int QueryCount { get; private set; }

    /// <summary>Gets how many hits it has returned across those queries.</summary>
    public int HitCount { get; private set; }

    /// <summary>
    /// Gets how many returned hits carried a tag other than the one filtered for — the leak count,
    /// which must stay at zero.
    /// </summary>
    public int LeakedHitCount { get; private set; }

    /// <summary>Gets how many returned hits carried no tag at all.</summary>
    /// <remarks>
    /// Separate from <see cref="LeakedHitCount"/> because it fails for a different reason: an
    /// untagged chunk means indexing missed it, not that the filter leaked. Folding the two together
    /// would report a tagging bug as a filtering bug.
    /// </remarks>
    public int UntaggedHitCount { get; private set; }

    /// <inheritdoc/>
    public override string Name => FormattableString.Invariant(
        $"+tag filter ({TagKey}={_keep}) over a {_corpusCount}-corpus store");

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<ChunkHit>> RetrieveAsync(
        BeirQuery query,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        InMemoryVectorStore store,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        var vectors = await BeirHarness.EmbedAsync(
            generator, embeddings, [query.Text], cancellationToken);

        // The harness computes TopK and MinScore from the cutoff and the units' shape; carrying them
        // across matters. Building a fresh SearchOptions with only the filter set would silently
        // search at the default TopK of 5 and score every cell short.
        var filtered = new SearchOptions
        {
            TopK = options.TopK,
            MinScore = options.MinScore,
            MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                [TagKey] = _keep,
            },
        };

        var results = await store.SearchAsync(vectors[0], filtered, cancellationToken);

        for (var i = 0; i < results.Count; i++)
        {
            if (!results[i].Chunk.Metadata.TryGetValue(TagKey, out var tag))
            {
                UntaggedHitCount++;
            }
            else if (!string.Equals(tag.StringValue, _keep, StringComparison.Ordinal))
            {
                LeakedHitCount++;
            }
        }

        QueryCount++;
        HitCount += results.Count;

        return ToChunkHits(results);
    }

    /// <summary>
    /// Asserts the filter held: every hit this row returned carried the tag it filtered for.
    /// </summary>
    /// <param name="datasetName">Names the run in the failure message.</param>
    /// <remarks>
    /// Checked before the figure is read, and separately from it. A filter can leak on queries whose
    /// leaked documents are not judged — the metric would not move and the cell would report a clean
    /// number for a broken boundary. Callers reasonably read a metadata filter as a boundary rather
    /// than a ranking hint, which is precisely the reading issue #350 found to be unsafe elsewhere.
    /// </remarks>
    public void AssertEveryHitCarriedTheTag(string datasetName)
    {
        Assert.True(
            QueryCount > 0 && HitCount > 0,
            $"{datasetName}: the tag-filtered row returned no hits at all, so there is no evidence " +
            "to judge. An empty run passes a leak check trivially.");

        Assert.True(
            UntaggedHitCount == 0,
            FormattableString.Invariant(
                $"{datasetName}: {UntaggedHitCount} of {HitCount} returned hits carry no '{TagKey}' ") +
            "metadata at all. That is an INDEXING failure, not a filtering one — units reached the " +
            "store untagged — and it would make the leak count below meaningless, since an untagged " +
            "chunk cannot be recognised as belonging to the other corpus.");

        Assert.True(
            LeakedHitCount == 0,
            FormattableString.Invariant(
                $"{datasetName}: {LeakedHitCount} of {HitCount} returned hits carry a '{TagKey}' ") +
            FormattableString.Invariant($"other than '{_keep}'. The filter LEAKED: chunks the ") +
            "filter excludes reached a ranking. A caller reading a metadata filter as a boundary — " +
            "which is how it documents itself — would be wrong.");
    }
}
