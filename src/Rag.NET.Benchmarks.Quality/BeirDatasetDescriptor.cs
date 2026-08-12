using System.Collections.Frozen;

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
/// <param name="TitledDocumentCount">
/// Documents in <c>corpus.jsonl</c> whose <c>title</c> is present and non-empty.
/// <para>
/// Recorded because it decides whether the title/text separator can move a number at all. FiQA's
/// corpus has <b>no</b> titles, so <c>title + sep + text</c> trims back to the same bytes for every
/// separator and measuring both is an hour of duplicated work that cannot report anything. SciFact
/// titles all 5,183; ArguAna titles 2,699 of 8,674.
/// </para>
/// </param>
/// <param name="ExcludesSelfRetrievedDocument">
/// Whether the retrieval protocol drops the corpus document whose id equals the query id, before the
/// ranking is cut to <c>k</c>.
/// <para>
/// This is MTEB's <c>ignore_identical_ids</c>, and it is a property of the dataset rather than a
/// preference: <c>mteb.tasks.Retrieval.eng.ArguAnaRetrieval</c> and <c>FiQA2018Retrieval</c> both set
/// it to <see langword="true"/>, <c>SciFactRetrieval</c> leaves it at <c>AbsTaskRetrieval</c>'s
/// <see langword="false"/>. BEIR's own <c>DenseRetrievalExactSearch.search</c> applies it
/// unconditionally (<c>if corpus_id != query_id</c>), so the figures on both sides embed it.
/// </para>
/// <para>
/// <b>ArguAna is unrunnable without it.</b> 1,298 of its 1,406 queries are byte-identical to the
/// corpus document sharing their id — the queries <i>are</i> arguments and the arguments are in the
/// corpus — so self-similarity 1.0 takes rank 1 on 92% of the dataset and pushes the one relevant
/// counterargument to rank 2. That alone would cost roughly 1 − 1/log₂3 ≈ 0.37 of the ideal gain on
/// those queries, which is an order of magnitude outside the parity band.
/// </para>
/// </param>
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
    int TitledDocumentCount,
    bool ExcludesSelfRetrievedDocument,
    BeirParityTarget ParityTarget)
{
    // Held as the concrete FrozenSet rather than as the interface the property exposes: the
    // interface would box the set's value-type enumerator on assignment (HLQ001), and Supports
    // reads this field directly so its Contains call is a direct one rather than a virtual
    // dispatch through IReadOnlySet.
    private readonly FrozenSet<BeirProtocol>? _applicableProtocols;

    /// <summary>
    /// The protocols this dataset can be measured under, or <see langword="null"/> for all of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because "nobody has run this" and "running this would be meaningless" were the
    /// same statement.</b> Both were written as an empty <c>BeirReproduction</c> array beside a
    /// <c>FitsTheNightly: false</c> budget cell reading NEVER RUN. TREC-COVID's Comparison cell means
    /// the first. MultiHop-RAG's Parity leg means the second — its articles average 10,340 characters
    /// and the parity protocol indexes one chunk per document truncated at 256 tokens, so the run
    /// would score roughly the first tenth of each article and report the result as retrieval quality.
    /// </para>
    /// <para>
    /// Conflating the two is not untidiness. <see cref="All"/> enrols a descriptor in every theory
    /// that iterates it, so on 2026-08-12 a descriptor added for Phase 5.3 joined the comparison
    /// control and killed a cost sweep seven minutes in against a cold embedding cache.
    /// </para>
    /// <para>
    /// <see langword="null"/> rather than a populated set is deliberate: it keeps the four existing
    /// descriptors byte-identical and makes "supports everything" the thing you get by not thinking
    /// about it, which is right for a BEIR dataset and wrong only for the shapes BEIR does not cover.
    /// </para>
    /// <para>
    /// Two things are settled on assignment rather than left to a caller, because a descriptor is
    /// usually a <see langword="static"/> field read by every theory in the harness.
    /// </para>
    /// <para>
    /// <b>An empty set is refused.</b> It would make <see cref="Supports"/> answer
    /// <see langword="false"/> for every protocol, which is a descriptor that is described and then
    /// measured by nothing — precisely the state <see cref="All"/>'s own documentation says cannot
    /// arise and that
    /// <c>All_ListsEveryDescribedDataset_SoADescriptorCannotExistWithoutBeingMeasured</c> exists to
    /// prevent. Neither would catch it, because both look at whether a descriptor is listed rather
    /// than at whether anything can run it. Failing at construction beats surfacing hours later as a
    /// universal skip nobody can account for.
    /// </para>
    /// <para>
    /// <b>And the set is copied.</b> <see cref="IReadOnlySet{T}"/> is a read-only view, not an
    /// immutable set: a caller holding the <see cref="HashSet{T}"/> it passed can keep mutating it
    /// afterwards and silently change what a <see langword="static"/> descriptor supports at
    /// runtime. Freezing on the way in makes the descriptor's answer a function of construction.
    /// </para>
    /// </remarks>
    public IReadOnlySet<BeirProtocol>? ApplicableProtocols
    {
        get => _applicableProtocols;
        init
        {
            if (value is not null && value.Count == 0)
            {
                throw new ArgumentException(
                    "A BEIR dataset descriptor cannot declare an empty set of applicable " +
                    "protocols: it would be measurable under no protocol at all, so it would be " +
                    "described and then run by nothing. Pass null to say 'all of them', which is " +
                    "the default, or name the protocols the dataset can actually be measured under.",
                    nameof(value));
            }

            _applicableProtocols = value?.ToFrozenSet();
        }
    }

    /// <summary>Reports whether this dataset can be measured under one protocol.</summary>
    /// <param name="protocol">The protocol to ask about.</param>
    /// <returns><see langword="true"/> when this dataset can be measured under it.</returns>
    public bool Supports(BeirProtocol protocol) =>
        _applicableProtocols is null || _applicableProtocols.Contains(protocol);

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
        TitledDocumentCount: 5183,
        ExcludesSelfRetrievedDocument: false,
        ParityTarget: new BeirParityTarget(0.645, SciFactPublishedSource));

    /// <summary>
    /// FiQA-2018 task 2: opinionated financial questions against answers crawled from StackExchange,
    /// Reddit and StockTwits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts are from the downloaded archive, not from a paper: 57,638 documents, 6,648 queries,
    /// and 1,706 judgements over 648 distinct query ids in <c>qrels/test.tsv</c>. BEIR's README table
    /// agrees — 648 test queries, a 57K corpus, 2.6 relevant documents per query, and the MD5 below —
    /// and so does MTEB's <c>FiQA2018</c> task metadata, which records
    /// <c>num_documents = 57638</c>, <c>num_queries = 648</c> and
    /// <c>average_relevant_docs_per_query = 2.6327…</c>, exactly 1706 ÷ 648.
    /// </para>
    /// <para>
    /// <b>Not one relevant document per query.</b> SciFact's IDCG is 1 for 92% of its queries;
    /// FiQA's is not, because only 220 of the 648 judged queries have a single relevant document
    /// (184 have two, 103 three, and the tail runs to 15). Its nDCG therefore exercises the IDCG
    /// arithmetic that SciFact leaves almost entirely at 1.
    /// </para>
    /// <para>
    /// <b>The corpus has no titles at all</b> — every one of the 57,638 lines has an empty
    /// <c>title</c> — and 51.0% of its documents exceed <c>ChunkingOptions.MaxChunkSize</c>.
    /// </para>
    /// <para>
    /// <b>That fraction is not what makes max-pooling stop being a no-op, and this remark used to
    /// claim it was.</b> Pooling is a no-op under the <i>parity</i> protocol on <i>every</i>
    /// dataset — FiQA included — because that protocol indexes one chunk per document by
    /// construction, and parity is the only protocol any published figure is comparable to. Under
    /// the <i>real</i> protocol it is a no-op on none of them: SciFact's real leg pooled on all
    /// 1,109 of its queries and ArguAna's on all 1,406 — counts taken when the harness retrieved
    /// for every query in <c>queries.jsonl</c>; it now retrieves only the judged ones, so SciFact's
    /// counter reads at most 300 on a re-run. 51.0% is in fact the <i>lowest</i>
    /// over-size fraction of the three — SciFact 99.2%, ArguAna 87.3%. What is genuinely FiQA's
    /// alone is the scale it is measured over: 57,638 documents becoming 121,236 chunks under
    /// Phase 3.16's packing chunker, and 429,850 before it.
    /// </para>
    /// </remarks>
    public static BeirDatasetDescriptor FiQA { get; } = new(
        "fiqa",
        new Uri("https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/fiqa.zip"),
        "17918ed23cd04fb15047f73e6c3bd9d9",
        FiQALicence,
        DocumentCount: 57638,
        QueryCount: 6648,
        TestQueryCount: 648,
        TitledDocumentCount: 0,
        ExcludesSelfRetrievedDocument: true,
        ParityTarget: new BeirParityTarget(0.36867, FiQAPublishedSource));

    /// <summary>
    /// ArguAna: retrieve the best counterargument to an argument, over idebate.org debates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts are from the downloaded archive, not from a paper: 8,674 documents, 1,406 queries, and
    /// 1,406 judgements over 1,406 distinct query ids in <c>qrels/test.tsv</c> — exactly one relevant
    /// document per query, and every query judged. BEIR's README table agrees (1,406 queries, an
    /// 8.67K corpus, 1.0 relevant documents per query, and the MD5 below), as does MTEB's
    /// <c>ArguAna</c> task metadata.
    /// </para>
    /// <para>
    /// <b>Every query is judged</b>, which no other dataset here manages: SciFact evaluates 300 of
    /// 1,109 and FiQA 648 of 6,648. It is therefore the one dataset on which the harness's
    /// judged-only retrieval filters nothing out — which is why ArguAna's per-run counters were
    /// unaffected when that filter was introduced, and why its HyDE cell passed while the others'
    /// refuse-on-miss caches failed on the first unjudged query.
    /// </para>
    /// <para>
    /// <b>And the queries are in the corpus.</b> 1,298 of the 1,406 query texts are byte-identical to
    /// the corpus document sharing their id, which is why
    /// <see cref="ExcludesSelfRetrievedDocument"/> is set — see its own documentation for what
    /// happens without it.
    /// </para>
    /// </remarks>
    public static BeirDatasetDescriptor ArguAna { get; } = new(
        "arguana",
        new Uri("https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/arguana.zip"),
        "8ad3e3c2a5867cdced806d6503f29b99",
        ArguAnaLicence,
        DocumentCount: 8674,
        QueryCount: 1406,
        TestQueryCount: 1406,
        TitledDocumentCount: 2699,
        ExcludesSelfRetrievedDocument: true,
        ParityTarget: new BeirParityTarget(0.50167, ArguAnaPublishedSource));

    /// <summary>
    /// TREC-COVID: 50 topics judged against the CORD-19 open-research corpus, and the programme's
    /// <b>first graded-relevance dataset</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts measured from the downloaded archive on 2026-08-11, not from a paper: 171,332
    /// documents, 50 queries, and 66,336 judgements over 50 query ids in <c>qrels/test.tsv</c>.
    /// 171,325 of the documents carry a title; 7 do not.
    /// </para>
    /// <para>
    /// <b>It is genuinely graded, which FiQA turned out not to be.</b> The judgements are
    /// 41,661 at grade 0, 10,456 at grade 1 and 14,217 at grade 2 — so <c>2^rel - 1</c> finally
    /// evaluates to something other than 1 (grade 2 gains 3), and Milestone 5's graded-gain
    /// criterion has a dataset that can satisfy it. NIST states the scale verbatim: "judgment is 0
    /// for not relevant, 1 for partially relevant, and 2 for fully relevant". FiQA's equivalent
    /// claim was checked the same way and failed — every one of its 17,110 judgements across all
    /// three splits scores exactly 1.
    /// </para>
    /// <para>
    /// <b>Two judgements score -1, a value the documented scale does not contain</b> (query 38 /
    /// <c>9hbib8b3</c> and query 50 / <c>svo94kuo</c>). They are already handled: every consumer in
    /// <see cref="IrMetrics"/> gates on <c>grade &gt; 0</c>, so a negative grade is treated as
    /// non-relevant and never reaches <c>Gain</c> — which would otherwise return -0.5 for it. That
    /// is also what <c>trec_eval</c> does with non-positive judgements, so the boundary this
    /// repository publishes across stays honest. The count is recorded here because a reader
    /// totalling the qrels will otherwise be two rows out: 41,661 + 10,456 + 14,217 + 2 = 66,336.
    /// </para>
    /// <para>
    /// <b>Densely judged, unlike the other three.</b> Every one of the 50 queries has at least 111
    /// relevant documents and one has 1,266, averaging 493.5 — against SciFact's 1.1. nDCG@10's
    /// IDCG is therefore always the full ideal ten, never the short-list case SciFact exercises,
    /// and Recall@10 is bounded far below 1 by construction: ten documents cannot recall 493.
    /// </para>
    /// </remarks>
    public static BeirDatasetDescriptor TrecCovid { get; } = new(
        "trec-covid",
        new Uri("https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/trec-covid.zip"),
        "ce62140cb23feb9becf6270d0d1fe6d1",
        TrecCovidLicence,
        DocumentCount: 171332,
        QueryCount: 50,
        TestQueryCount: 50,
        TitledDocumentCount: 171325,
        ExcludesSelfRetrievedDocument: false,
        ParityTarget: new BeirParityTarget(0.47232, TrecCovidPublishedSource));

    /// <summary>
    /// Every dataset the harness knows about, in the order they were added.
    /// </summary>
    /// <remarks>
    /// The parity test enumerates this, so a dataset that is described here is measured. There is no
    /// second list to keep in step and no way to add a descriptor that nothing runs.
    /// </remarks>
    public static IReadOnlyList<BeirDatasetDescriptor> All { get; } = [SciFact, FiQA, ArguAna, TrecCovid];

    /// <summary>
    /// Where every published figure in this file was read from, quoted into each dataset's own
    /// source string so a number and its provenance cannot drift apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MTEB's official results repository, <c>github.com/embeddings-benchmark/results</c>, under
    /// <c>results/sentence-transformers__all-MiniLM-L6-v2/8b3219a92973c328a8e22fadcfa821b5dc75636a/</c>
    /// — that path segment is the model's own Hugging Face commit, so the figures are pinned to a
    /// revision of the model and not merely to its name. All three were produced by
    /// <c>mteb_version 1.12.75</c>. This is the leaderboard's data source rather than the rendered
    /// leaderboard, because the rendered one carries no revision and rounds.
    /// </para>
    /// <para>
    /// <b>The BEIR paper is not a second opinion on any of them.</b> Thakur et al. 2021 evaluate ten
    /// systems and <c>all-MiniLM-L6-v2</c> is not among them; the only MiniLM in the paper is
    /// <c>cross-encoder/ms-marco-MiniLM-L-6-v2</c>, the BM25+CE re-ranker listed in its Table 6 of
    /// model links. So the plan's "MTEB and the BEIR paper sometimes differ" cannot apply here —
    /// there is one source, not two that agree.
    /// </para>
    /// </remarks>
    private const string MtebResultsSource =
        "MTEB official results, github.com/embeddings-benchmark/results at " +
        "results/sentence-transformers__all-MiniLM-L6-v2/8b3219a92973c328a8e22fadcfa821b5dc75636a " +
        "(mteb_version 1.12.75). The BEIR paper is not a cross-check: it does not evaluate " +
        "all-MiniLM-L6-v2 at all — its only MiniLM is the ms-marco-MiniLM-L-6-v2 cross-encoder.";

    /// <summary>
    /// The provenance of SciFact's published figure: the constant Phase 3.7 measured against, now
    /// with the source it never had.
    /// </summary>
    /// <remarks>
    /// Phase 3.7 measured against a bare <c>0.645</c> and cited nothing for it — neither its design,
    /// its plan nor <c>docs/reference/retrieval-quality.md</c> named where the figure came from.
    /// Looking up FiQA's and ArguAna's figures found SciFact's as a by-product: MTEB reports
    /// <c>0.64508</c> for this model on SciFact's test split, which rounds to the 0.645 that was
    /// carried unsourced for two phases. The target is left at 0.645 rather than tightened to
    /// 0.64508, because moving it would change what the phase's regression gate asserts for a gain of
    /// 0.001; what changes here is that the figure now has a citation.
    /// </remarks>
    private const string SciFactPublishedSource =
        "0.645 for all-MiniLM-L6-v2, carried verbatim from the constant Phase 3.7 measured against " +
        "and unsourced until now. Corroborated at SciFact.json ndcg_at_10 = 0.64508 (test split, " +
        "dataset revision 0228b52cf27578f30900b9e5271d331663a030d7) from " + MtebResultsSource +
        " The measured value under this harness is 0.64593.";

    /// <summary>The provenance of TREC-COVID's published figure.</summary>
    /// <remarks>
    /// One split only: TREC-COVID ships nothing but <c>test</c>, which is also the split
    /// <c>qrels/test.tsv</c> holds and the one this descriptor's counts describe.
    /// </remarks>
    private const string TrecCovidPublishedSource =
        "0.47232 for all-MiniLM-L6-v2 -- TRECCOVID.json ndcg_at_10, test split, dataset revision " +
        "bb9466bac8153a0349341eb1b22e06409e78ef4e, from " + MtebResultsSource;

    /// <summary>The provenance of FiQA's published figure.</summary>
    /// <remarks>
    /// <c>FiQA2018.json</c> reports three splits; the <c>test</c> one is taken, because that is the
    /// split <c>qrels/test.tsv</c> holds and the one the descriptor's counts describe. Its siblings
    /// are close enough to be mistaken for it and far enough to fail the band: <c>dev</c> is 0.38815
    /// and <c>train</c> 0.36609.
    /// </remarks>
    private const string FiQAPublishedSource =
        "0.36867 for all-MiniLM-L6-v2 — FiQA2018.json ndcg_at_10, test split, dataset revision " +
        "27a168819829fe9bcd655c2df245fb19452e8e06, from " + MtebResultsSource +
        " The dev split reports 0.38815 and train 0.36609; test is the split measured here.";

    /// <summary>The provenance of ArguAna's published figure.</summary>
    /// <remarks>
    /// One split only, since ArguAna ships nothing but <c>test</c>. The MTEB run that produced this
    /// figure sets <c>ignore_identical_ids = True</c>, which is recorded on the descriptor as
    /// <see cref="ExcludesSelfRetrievedDocument"/> — the figure is not reproducible without it.
    /// </remarks>
    private const string ArguAnaPublishedSource =
        "0.50167 for all-MiniLM-L6-v2 — ArguAna.json ndcg_at_10, test split, dataset revision " +
        "c22ab2a51041ffd869aaddef7af8d8215647e41a, from " + MtebResultsSource +
        " That run sets ignore_identical_ids = True; the figure is not reproducible without it.";

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

    /// <summary>
    /// FiQA's terms, read from the challenge site rather than from a mirror, and the sharpest
    /// disagreement in this file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream is <c>https://sites.google.com/view/fiqa/</c>, the WWW'18 open challenge site, and it
    /// names no licence at all. What it states, verbatim and twice, is: "The training data is
    /// available only for non-commercial use" and "The testing data is available only for
    /// non-commercial use". It also records where the collection came from: "The QA test collection
    /// is built by crawling Stackexchange, Reddit and StockTwits" — three corpora with terms of their
    /// own that the challenge does not restate.
    /// </para>
    /// <para>
    /// <b>Disagreement, recorded rather than resolved, and this one is material.</b> The Hugging Face
    /// mirror <c>BeIR/fiqa</c> declares <c>cc-by-sa-4.0</c>, which <i>permits</i> commercial use — it
    /// grants strictly more than upstream does, in the one direction upstream explicitly refuses.
    /// MTEB's mirror <c>mteb/fiqa</c> declares <c>unknown</c>, which is at least honest. Upstream is
    /// treated as authoritative here, so this dataset is downloaded for measurement and nothing else.
    /// </para>
    /// <para>
    /// Note that <c>BeIR/fiqa</c>, <c>BeIR/arguana</c> and <c>BeIR/scifact</c> all declare the same
    /// <c>cc-by-sa-4.0</c>. That is a blanket declaration across the mirror rather than a per-dataset
    /// determination, which is why it disagrees with all three upstreams and is worth trusting on
    /// none of them.
    /// </para>
    /// <para>
    /// Cite: Maia et al., "WWW'18 Open Challenge: Financial Opinion Mining and Question Answering",
    /// WWW 2018 Companion.
    /// </para>
    /// </remarks>
    private const string FiQALicence =
        "No licence is named upstream. https://sites.google.com/view/fiqa/ states only that \"The " +
        "training data is available only for non-commercial use\" and \"The testing data is " +
        "available only for non-commercial use\", over a collection \"built by crawling " +
        "Stackexchange, Reddit and StockTwits\" whose own terms the challenge does not restate. The " +
        "Hugging Face mirror BeIR/fiqa declares cc-by-sa-4.0, which permits the commercial use " +
        "upstream refuses; mteb/fiqa declares unknown. Upstream is authoritative: this is downloaded " +
        "for measurement and not redistributed. Cite: Maia et al., WWW 2018 Companion.";

    /// <summary>
    /// ArguAna's licence, read from the upstream deposit rather than from a mirror, because the
    /// homepage BEIR links has gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// BEIR's README links <c>http://argumentation.bplaced.net/arguana/data</c>, which now answers
    /// 404. The live upstream is the Webis deposit on Zenodo — DOI
    /// <c>10.5281/zenodo.3973258</c>, concept DOI <c>10.5281/zenodo.3973257</c>, linked from
    /// <c>https://webis.de/data/arguana-counterargs.html</c> — whose record declares
    /// <c>cc-by-4.0</c>. It describes itself as "6753 pairs of argument and best counterargument
    /// from the online debate portal idebate.org"; idebate.org's own terms are not restated by the
    /// deposit and are not asserted here.
    /// </para>
    /// <para>
    /// <b>Disagreement, recorded rather than resolved.</b> Both mirrors — <c>BeIR/arguana</c> and
    /// <c>mteb/arguana</c> — declare <c>cc-by-sa-4.0</c>, adding a share-alike obligation upstream's
    /// CC BY 4.0 does not impose. This is the same shape as SciFact's disagreement and the same
    /// resolution: upstream is authoritative.
    /// </para>
    /// <para>
    /// Cite: Wachsmuth, Syed and Stein, "Retrieval of the Best Counterargument without Prior Topic
    /// Knowledge", ACL 2018.
    /// </para>
    /// </remarks>
    /// <summary>
    /// TREC-COVID's licence, determined from the corpus's own agreement rather than from a mirror.
    /// </summary>
    /// <remarks>
    /// The BEIR Hugging Face card <c>BeIR/trec-covid</c> is tagged <c>cc-by-sa-4.0</c>, and that
    /// tag is a blanket repository tag rather than a determination — BEIR's own paper states that
    /// the authors of several of its datasets report no licence at all. Upstream is authoritative,
    /// and upstream here is the CORD-19 dataset agreement.
    /// </remarks>
    private const string TrecCovidLicence =
        "Corpus: the CORD-19 dataset agreement, which permits text and data mining only " +
        "(https://github.com/allenai/cord19, LICENSE). Judgements: NIST TREC-COVID topics and " +
        "qrels (https://ir.nist.gov/trec-covid/), publicly released for research use. The " +
        "agreement permits exactly what this repository does with it -- cache locally, benchmark, " +
        "publish figures -- and forbids redistribution, which this repository does not do: no " +
        "corpus, judgement or embedding file is ever committed. Do not cite the Hugging Face " +
        "BeIR/trec-covid card's cc-by-sa-4.0 tag; it is a blanket repo tag contradicted upstream.";

    private const string ArguAnaLicence =
        "CC BY 4.0, from the upstream Zenodo deposit doi:10.5281/zenodo.3973258 (Webis, " +
        "https://webis.de/data/arguana-counterargs.html). BEIR's linked homepage " +
        "http://argumentation.bplaced.net/arguana/data is dead. The arguments are crawled from the " +
        "debate portal idebate.org, whose own terms the deposit does not restate. The Hugging Face " +
        "mirrors BeIR/arguana and mteb/arguana both declare cc-by-sa-4.0, adding a share-alike " +
        "obligation upstream does not impose; upstream is authoritative. Cite: Wachsmuth, Syed and " +
        "Stein, ACL 2018.";

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
