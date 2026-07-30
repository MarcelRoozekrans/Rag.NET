namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Turns a chunk-level retrieval result into the <b>document</b>-level ranking BEIR is evaluated on.
/// <para>
/// BEIR's qrels map query id to <i>document</i> id and nDCG@10 ranks documents. Rag.NET retrieves
/// chunks. Handing chunk hits straight to <see cref="IrMetrics"/> computes a different quantity that
/// merely resembles nDCG@10 — and it fails silently rather than loudly, because a chunk id simply
/// never matches a qrels entry.
/// </para>
/// <para>The order is fixed, and each step is pinned by a test:</para>
/// <list type="number">
///   <item>map each retrieved chunk to its parent document</item>
///   <item><b>max-pool</b> to one score per document</item>
///   <item>dedupe</item>
///   <item><i>then</i> take the top <c>k</c></item>
/// </list>
/// <para>
/// Cutting to top <c>k</c> first gives a different answer, and the difference is documents going
/// missing rather than documents being reordered: a document contributing several strong chunks
/// occupies several of the <c>k</c> slots and evicts rivals that pooling would have kept. Measured on
/// <c>DocumentRankingTests</c>' fixture — four chunks from one document among seven — cutting first
/// returns two documents for <c>k = 3</c> and scores nDCG@3 = 0 on a query that pooling first scores
/// 0.5. Against a parity band of ±0.02, that is not a rounding difference.
/// </para>
/// <para>
/// It also bites unevenly, which is what makes it dangerous. SciFact abstracts and ArguAna arguments
/// are mostly single-chunk, so those numbers look plausible either way; FiQA and TREC-COVID have long
/// documents where the discrepancy is real. A table that is right in the cheap places and wrong in
/// the expensive ones is worse than no table.
/// </para>
/// </summary>
public static class DocumentRanking
{
    /// <summary>
    /// Ties on the pooled score are broken by ordinal document id. Arbitrary, but repeatable — and
    /// repeatable is the requirement, since <see cref="Array.Sort{T}(T[], Comparison{T})"/> is not a
    /// stable sort and in-memory storage is pinned precisely so the harness is deterministic. Cached
    /// rather than written inline so the delegate is allocated once for the whole run.
    /// </summary>
    private static readonly Comparison<ScoredDocument> ByPooledScoreThenDocumentId =
        static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.DocumentId, right.DocumentId);
        };

    /// <summary>
    /// Max-pools <paramref name="chunkHits"/> to one entry per parent document, then returns the best
    /// <paramref name="k"/> of those documents, best first.
    /// </summary>
    /// <param name="chunkHits">
    /// The retrieved chunks with their parent document ids, in any order — this method sorts, so the
    /// caller's ordering is irrelevant and no assumption is made about it.
    /// </param>
    /// <param name="k">How many documents to return. Must be positive.</param>
    /// <returns>
    /// At most <paramref name="k"/> distinct documents, best pooled score first, ties broken by
    /// ordinal document id. Fewer than <paramref name="k"/> when the hits name fewer distinct
    /// documents; empty when there are no hits, which is a query that retrieved nothing and scores
    /// zero rather than an error.
    /// </returns>
    /// <remarks>
    /// The cut happens <b>after</b> pooling. Reversing the two lets one heavily chunked document
    /// consume several of the <paramref name="k"/> slots and evict documents that belong in the
    /// ranking.
    /// </remarks>
    public static IReadOnlyList<ScoredDocument> TopDocuments(IReadOnlyList<ChunkHit> chunkHits, int k)
    {
        ArgumentNullException.ThrowIfNull(chunkHits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        // Steps 1-3: parent document id is the key, so pooling and deduplication are the same pass.
        // Max, not sum — which would reward a document for being chunked into more pieces — and not
        // mean, which would punish a document for holding one strongly matching passage among many
        // unrelated ones.
        var pooledByDocument = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < chunkHits.Count; i++)
        {
            var hit = chunkHits[i];
            if (hit is null)
            {
                throw new ArgumentException($"chunkHits[{i}] is null.", nameof(chunkHits));
            }

            if (!pooledByDocument.TryGetValue(hit.DocumentId, out var pooled) || hit.Score > pooled)
            {
                pooledByDocument[hit.DocumentId] = hit.Score;
            }
        }

        var documents = new ScoredDocument[pooledByDocument.Count];
        var next = 0;
        foreach (var (documentId, score) in pooledByDocument)
        {
            documents[next++] = new ScoredDocument(documentId, score);
        }

        Array.Sort(documents, ByPooledScoreThenDocumentId);

        // Step 4, and only now.
        return documents.AsSpan(0, Math.Min(k, documents.Length)).ToArray();
    }

    /// <summary>
    /// <see cref="TopDocuments"/> projected to bare document ids — the shape
    /// <see cref="IrMetrics.NormalizedDiscountedCumulativeGain"/> and its siblings take.
    /// </summary>
    /// <param name="chunkHits">The retrieved chunks with their parent document ids.</param>
    /// <param name="k">How many documents to return. Must be positive.</param>
    /// <returns>At most <paramref name="k"/> distinct document ids, best first.</returns>
    public static IReadOnlyList<string> TopDocumentIds(IReadOnlyList<ChunkHit> chunkHits, int k)
    {
        var documents = TopDocuments(chunkHits, k);
        var documentIds = new string[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            documentIds[i] = documents[i].DocumentId;
        }

        return documentIds;
    }
}
