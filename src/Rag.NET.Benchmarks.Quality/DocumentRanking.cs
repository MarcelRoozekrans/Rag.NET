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
/// <b>It also bites unevenly — but this paragraph named the wrong datasets, and Phase 3.12 measured
/// them.</b> It used to say SciFact abstracts and ArguAna arguments "are mostly single-chunk", so
/// those numbers "look plausible either way", and that FiQA is where the discrepancy is real. All of
/// that is false: at stock 512-character chunking SciFact's 5,183 abstracts produce 56,707 units, up
/// to 221 from one document, and ArguAna's 8,674 arguments produce 82,618, up to 285. 99.2% of
/// SciFact's documents exceed the chunk size and 87.3% of ArguAna's — more, in both cases, than
/// FiQA's 51.0%.
/// </para>
/// <para>
/// What is true is narrower, and is a property of the <i>protocol</i> rather than of any corpus: the
/// order cannot bite at all under the parity protocol, which indexes one chunk per document, and it
/// bites on every query of both measured datasets under the real one — SciFact pooled on 1,109 of
/// 1,109 queries and ArguAna on 1,406 of 1,406. A table that is right in the cheap places and wrong
/// in the expensive ones is still worse than no table; the mistake was in guessing which places were
/// which without measuring.
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
    /// <param name="excludedDocumentId">
    /// A document id to drop before pooling, or <see langword="null"/> to keep everything. Passed the
    /// query's own id when the dataset's
    /// <see cref="BeirDatasetDescriptor.ExcludesSelfRetrievedDocument"/> is set — see the remarks.
    /// </param>
    /// <returns>
    /// At most <paramref name="k"/> distinct documents, best pooled score first, ties broken by
    /// ordinal document id. Fewer than <paramref name="k"/> when the hits name fewer distinct
    /// documents; empty when there are no hits, which is a query that retrieved nothing and scores
    /// zero rather than an error.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The cut happens <b>after</b> pooling. Reversing the two lets one heavily chunked document
    /// consume several of the <paramref name="k"/> slots and evict documents that belong in the
    /// ranking.
    /// </para>
    /// <para>
    /// <b>The exclusion happens before pooling too</b>, and for the same reason: a document dropped
    /// after the cut leaves a hole in the ranking rather than promoting the document behind it, so
    /// nDCG@k would be computed over k−1 results on some queries and k on others.
    /// </para>
    /// <para>
    /// <paramref name="excludedDocumentId"/> is BEIR's <c>if corpus_id != query_id</c> and MTEB's
    /// <c>ignore_identical_ids</c>. It is a protocol decision, not a convenience: on ArguAna the
    /// query text <i>is</i> a corpus document, so without it 92% of queries retrieve themselves at
    /// cosine 1.0 and the relevant counterargument never sees rank 1.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ScoredDocument> TopDocuments(
        IReadOnlyList<ChunkHit> chunkHits, int k, string? excludedDocumentId = null)
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

            if (string.Equals(hit.DocumentId, excludedDocumentId, StringComparison.Ordinal))
            {
                continue;
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
    /// <param name="excludedDocumentId">
    /// A document id to drop before pooling, or <see langword="null"/> to keep everything.
    /// </param>
    /// <returns>At most <paramref name="k"/> distinct document ids, best first.</returns>
    public static IReadOnlyList<string> TopDocumentIds(
        IReadOnlyList<ChunkHit> chunkHits, int k, string? excludedDocumentId = null)
    {
        var documents = TopDocuments(chunkHits, k, excludedDocumentId);
        var documentIds = new string[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            documentIds[i] = documents[i].DocumentId;
        }

        return documentIds;
    }
}
