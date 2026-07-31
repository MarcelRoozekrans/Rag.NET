namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Everything <see cref="BeirDatasetCache"/> needs to fetch and verify one BEIR dataset, plus the
/// counts and licence a caller needs to trust what it got.
/// </summary>
/// <param name="Name">
/// The BEIR dataset name. It is also the archive's top-level folder and therefore the directory the
/// dataset extracts into.
/// </param>
/// <param name="ArchiveUrl">The published zip.</param>
/// <param name="ArchiveMd5">
/// The MD5 BEIR publishes for that zip, lower-case hex. Integrity, not security — it is the check
/// that turns a truncated or redirected download into a loud failure instead of a short corpus that
/// scores badly and looks like a retrieval bug.
/// </param>
/// <param name="Licence">
/// The dataset's licence, recorded here because BEIR licences differ per dataset and BEIR itself
/// publishes none: its README states only that it "downloaded and prepared public datasets" and
/// that "it remains the user's responsibility to determine whether you have permission to use the
/// dataset under the dataset's license".
/// </param>
/// <param name="DocumentCount">Documents in <c>corpus.jsonl</c>.</param>
/// <param name="QueryCount">
/// Lines in <c>queries.jsonl</c> — every query, judged or not, and usually far more than
/// <paramref name="TestQueryCount"/>.
/// </param>
/// <param name="TestQueryCount">Distinct query ids in <c>qrels/test.tsv</c>.</param>
/// <param name="ParityTarget">
/// The published nDCG@10 the parity run is measured against, and the band around it. Carried by the
/// dataset rather than by a test, so adding a dataset is adding a descriptor rather than copying a
/// test file and editing three constants out of it.
/// </param>
public sealed record BeirDatasetDescriptor(
    string Name,
    Uri ArchiveUrl,
    string ArchiveMd5,
    string Licence,
    int DocumentCount,
    int QueryCount,
    int TestQueryCount,
    BeirParityTarget ParityTarget)
{
    /// <summary>
    /// SciFact: scientific claims against a corpus of abstracts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts are from the downloaded archive, not from a paper: 5,183 documents, 1,109 queries, and
    /// 339 judgements over 300 distinct query ids in <c>qrels/test.tsv</c>. BEIR's own README table
    /// agrees — 300 test queries, a 5K corpus, 1.1 relevant documents per query, and the MD5 below.
    /// </para>
    /// <para>
    /// Those two numbers are the harness's two traps in concrete form. 300 of the 1,109 queries are
    /// judged in the test split, so evaluating all of them divides the mean by roughly 3.7. And 277
    /// of the 300 judged queries have exactly one relevant document (14 have two, 4 have three, 3
    /// have four, 2 have five), so IDCG must equal exactly 1 for 92% of the dataset.
    /// </para>
    /// </remarks>
    public static BeirDatasetDescriptor SciFact { get; } = new(
        "scifact",
        new Uri("https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/scifact.zip"),
        "5f7d1de60b170fc8027bb7898e2efca1",
        SciFactLicence,
        DocumentCount: 5183,
        QueryCount: 1109,
        TestQueryCount: 300,
        ParityTarget: new BeirParityTarget(0.645, SciFactPublishedSource));

    /// <summary>
    /// Every dataset the harness knows about, in the order they were added.
    /// </summary>
    /// <remarks>
    /// The parity test enumerates this, so a dataset that is described here is measured. There is no
    /// second list to keep in step and no way to add a descriptor that nothing runs.
    /// </remarks>
    public static IReadOnlyList<BeirDatasetDescriptor> All { get; } = [SciFact];

    /// <summary>
    /// The provenance of SciFact's published figure, recorded as it actually stands rather than
    /// invented.
    /// </summary>
    /// <remarks>
    /// Phase 3.7 measured against a bare <c>0.645</c> and cited nothing for it: neither its design,
    /// its plan nor <c>docs/reference/retrieval-quality.md</c> names where the figure was read from.
    /// This string is a verbatim carry-over of that constant and says so, because a fabricated
    /// citation would be worse than an acknowledged gap — every dataset added after this one is
    /// required to name its source.
    /// </remarks>
    private const string SciFactPublishedSource =
        "≈ 0.645 for all-MiniLM-L6-v2, carried verbatim from the constant Phase 3.7 measured " +
        "against. No source was recorded for it at the time and none is invented here; the " +
        "measured value under this harness is 0.64593.";

    /// <summary>
    /// SciFact's licence, read from the upstream repository rather than assumed, because the
    /// dataset is licensed in two pieces and the redistributions disagree with each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// From <c>https://github.com/allenai/scifact/blob/master/LICENSE.md</c>, verbatim: "All claims
    /// and evidence annotations -- in the files <c>claims_*.jsonl</c> -- are released under CC BY
    /// 4.0", and "The abstracts in the corpus -- in the file <c>corpus.jsonl</c> -- are part of the
    /// Semantic Scholar S2ORC dataset and are licensed under ODC-By 1.0". The repository's code is
    /// Apache 2.0, which does not apply to anything downloaded here.
    /// </para>
    /// <para>
    /// The split matters for BEIR's repackaging: BEIR's <c>corpus.jsonl</c> is the ODC-By 1.0 half,
    /// while its <c>queries.jsonl</c> and <c>qrels/</c> derive from the CC BY 4.0 claims. Both
    /// require attribution; neither is public domain.
    /// </para>
    /// <para>
    /// <b>Disagreement, recorded rather than resolved.</b> The Hugging Face mirror
    /// <c>BeIR/scifact</c> declares a single <c>cc-by-sa-4.0</c> for the whole dataset, which
    /// matches neither upstream licence and adds a share-alike obligation upstream does not impose.
    /// Upstream is treated as authoritative here. Anyone redistributing this data — as opposed to
    /// downloading it into a cache, which is all this harness does — should read both.
    /// </para>
    /// <para>
    /// Cite: Wadden et al., "Fact or Fiction: Verifying Scientific Claims", EMNLP 2020.
    /// </para>
    /// </remarks>
    private const string SciFactLicence =
        "corpus.jsonl: ODC-By 1.0 (Semantic Scholar S2ORC). queries.jsonl and qrels: CC BY 4.0 " +
        "(SciFact claims and evidence annotations, Wadden et al., EMNLP 2020). Attribution required " +
        "for both; see https://github.com/allenai/scifact/blob/master/LICENSE.md. The Hugging Face " +
        "mirror BeIR/scifact declares cc-by-sa-4.0 for the whole dataset, which disagrees with " +
        "upstream; upstream is authoritative.";

    /// <summary>Gets the archive's file name in the cache directory.</summary>
    public string ArchiveFileName => Name + ".zip";

    /// <summary>Finds the descriptor in <see cref="All"/> whose <see cref="Name"/> matches.</summary>
    /// <param name="name">The BEIR dataset name, matched ordinally.</param>
    /// <returns>The descriptor.</returns>
    /// <exception cref="ArgumentException">No dataset is described under that name.</exception>
    /// <remarks>
    /// A lookup by name exists so a test can be a theory over dataset <i>names</i>. Handing xUnit the
    /// descriptors themselves would put a non-serializable record into theory data, and the run would
    /// then be one unnamed case per dataset instead of one legible case per dataset.
    /// </remarks>
    public static BeirDatasetDescriptor ByName(string name)
    {
        foreach (var descriptor in All)
        {
            if (string.Equals(descriptor.Name, name, StringComparison.Ordinal))
            {
                return descriptor;
            }
        }

        throw new ArgumentException(
            $"No BEIR dataset is described under the name '{name}'.", nameof(name));
    }
}
