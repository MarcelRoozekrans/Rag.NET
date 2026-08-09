namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// One entrant's self-measured cost for one dataset, as carried by its <see cref="TimingsSidecar"/>
/// beside the <see cref="TrecRunFile"/> it was measured alongside.
/// <para>
/// <b>Raw samples only — no statistic is stored here.</b> Every field is something a single
/// entrant observed about itself in its own runtime; p50 and p99 are computed afterwards by
/// <see cref="LatencyStatistics"/>, once, for every entrant. An entrant that reported its own
/// percentile would put its own definition of "p99" into a table alongside four other definitions,
/// and the resulting column would be unreadable while looking fine.
/// </para>
/// </summary>
/// <param name="RunTag">
/// The entrant's run tag — the same token the run file carries in every line's last field, e.g.
/// <c>langchain-core-1.5.3</c>. Checked against the run file's own tag when the pair is read: a
/// sidecar tagged for one entrant beside another's run file is the one pairing error that produces
/// a complete, plausible, wrong row.
/// </param>
/// <param name="IndexingSeconds">
/// Wall-clock seconds spent building the index, and nothing else — not dataset loading, not
/// embedder construction, not the run-file write.
/// <para>
/// <b>This is index construction with embedding already paid for</b>, not "the cost of indexing".
/// Every entrant embeds through the vector cache, because a shared pinned embedder is the only
/// way the five stacks are comparable at all — and every vector the run needs is prefetched into
/// memory before the span starts, so embedding <b>and its disk I/O</b> are excluded by
/// construction. The exclusion is not pedantry: with one cache-file read per text inside the
/// span, identical runs differed by up to 23x on OS page-cache state alone. What remains is the
/// work each library does around the embeddings, which is the part that differs between them.
/// </para>
/// <para>
/// <b>One known bias, between ecosystems:</b> the Python harness discovers its chunk texts with
/// an untimed rehearsal build, so its timed build is the second in the process — warmed — while
/// the .NET rows need no rehearsal and time a first build; the bias pushes Python's figure down
/// relative to .NET's. It is acceptable because all three Python entrants get identical
/// treatment and indexing publishes per ecosystem, never cross-ecosystem — the roadmap's §6
/// decision.
/// </para>
/// </param>
/// <param name="QueryLatenciesMilliseconds">
/// Milliseconds per retrieval call, one entry per judged query, keyed by query id — the library's
/// own retrieval only, with the harness's pooling and self-exclusion outside the span because
/// those are deliberately identical across entrants.
/// <para>
/// Keyed rather than a flat list so the set of ids can be checked against the run file's: a
/// sidecar timing 300 queries beside a run file ranking 1,406 of them is measuring a different
/// run, and only the ids make that visible.
/// </para>
/// </param>
/// <param name="EmbeddingCacheHits">
/// Embedding-cache hits over the whole run — counted during the untimed prefetch pass, once per
/// requested text, so they say what the disk really held. Reported beside the misses so the
/// ratio, rather than a sentence someone wrote later, says whether the cache was warm.
/// </param>
/// <param name="EmbeddingCacheMisses">
/// Embedding-cache misses over the whole run. Misses can only occur in the untimed prefetch pass
/// — a harness refuses a cold cache unless warming was explicitly requested, and the timed spans
/// serve from memory — so <paramref name="IndexingSeconds"/> stays embedding-free either way;
/// <b>a non-zero value still marks a run that paid model time no warm run paid</b>, carried in
/// the data rather than in prose so a run that warmed the cache is visibly different rather than
/// footnoted.
/// </param>
/// <param name="UnitCount">
/// How many units — chunks, nodes, passages, whatever the library calls them — the corpus became.
/// Indexing seconds without it is a number with no denominator: two entrants that split the same
/// corpus into 5,183 and 41,000 units are not slow and fast at the same job.
/// </param>
/// <param name="MaxUnitsPerDocument">
/// The largest number of units any one document produced. The harness multiplies retrieval depth
/// by this factor before pooling, so it is the reason two entrants' per-query latencies were
/// measured at different depths — without it, a latency difference that is purely a depth
/// difference reads as a speed difference.
/// </param>
public sealed record EntrantTimings(
    string RunTag,
    double IndexingSeconds,
    IReadOnlyDictionary<string, double> QueryLatenciesMilliseconds,
    int EmbeddingCacheHits,
    int EmbeddingCacheMisses,
    int UnitCount,
    int MaxUnitsPerDocument);
