using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// What each measurement actually reported, and how far it may move before that is a regression
/// rather than a different computer.
/// <para>
/// <b>This is not a parity band and it is not a second opinion on anybody's published figure.</b>
/// <see cref="BeirParityTarget"/> holds MTEB's number and a ±0.02 band around it, and that band is
/// correct as what it is: a tolerance for agreeing with a figure produced by other software on other
/// hardware. It is the wrong instrument for the other question — <i>did this repository's own number
/// move</i> — because ±0.02 is wider than most defects. The reviewer of Phase 3.12 demonstrated
/// exactly that: under a cut-then-pool mutation of <see cref="DocumentRanking"/>, SciFact's real run
/// fell 0.65589 → 0.64008 and ArguAna's 0.42594 → 0.40612, and <b>both tests still passed</b>, one
/// on the ±0.02 published band and one on <see cref="BeirRealChunkingTests"/>' 0.5×–1.5× collapse
/// envelope.
/// </para>
/// <para>
/// <b>So this is a reproduction check, kept deliberately separate from the parity check.</b> Every
/// figure below is a number this repository produced, on the machine named in its entry, on the date
/// named in its entry. Nothing here claims agreement with the literature; the entries for the real
/// protocol could not, since no published figure exists for it at all.
/// </para>
/// <para>
/// <b>Why it is not an exact match.</b> The runs are deterministic on one machine — the same model
/// over the same corpus produces the same vectors, in-memory storage is pinned for that reason, and
/// <see cref="DocumentRanking"/> breaks score ties by ordinal document id so even equal scores order
/// repeatably. Across machines they are not: ONNX Runtime dispatches its kernels on the available
/// instruction set, so a vector can differ in its last bits and a near-tie can order the other way.
/// A test that demanded the fifth decimal on every runner would eventually go red for a reason
/// nobody could act on, and the usual response to that is to delete it.
/// </para>
/// </summary>
public static class BeirReproduction
{
    /// <summary>
    /// How far a re-measurement may sit from the recorded one: ±0.005.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chosen from both ends rather than picked. <b>The lower end</b> is what the check has to
    /// catch: the cut-then-pool mutation moves SciFact's real run by 0.0158 and ArguAna's by 0.0198,
    /// so anything below about 0.015 catches it — 0.005 does, three times over.
    /// </para>
    /// <para>
    /// <b>The upper end</b> is what it must not trip on. SciFact scores 300 judged queries, and one
    /// query whose relevant document slips from rank 1 to rank 2 costs 1 − 1/log₂3 ≈ 0.37 of that
    /// query's ideal gain, so a single rank flip moves the mean by roughly 0.37 ÷ 300 ≈ 0.0012.
    /// SciFact is the coarsest dataset here for that reason — ArguAna's 1,406 judged queries make
    /// its quantum ≈ 0.00026 — so a window narrower than about 0.005 is one that a handful of
    /// near-ties resolving the other way on another CPU can breach. 0.005 is four of SciFact's
    /// quanta.
    /// </para>
    /// <para>
    /// For the parity runs this is four times tighter than the published band. For the <i>real</i>
    /// runs it replaces an envelope of ±50%, which is where the whole gap was.
    /// </para>
    /// </remarks>
    public const double Tolerance = 0.005;

    /// <summary>
    /// Every dataset under every protocol, with what it measured — or with the fact that it never
    /// has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table with the same shape and the same rule as <see cref="BeirRunBudget"/>'s: a pair that
    /// is not in it throws rather than passing quietly, so a fourth dataset cannot join the suite
    /// with nothing pinning its number. Adding the descriptor fails here until somebody has run it.
    /// </para>
    /// <para>
    /// <b>An entry may record that no figure exists</b>, which is a different state from being
    /// absent — it is the state FiQA's real leg sat in from Phase 3.12 until Phase 3.15 measured it
    /// on 2026-08-02. <see cref="AssertReproduces"/> prints what such a run measured and asserts
    /// nothing about it, because an hours-long measurement whose only outcome is a failure telling
    /// the runner to write down what they just watched is a measurement people learn to skip. No
    /// case is in that state today; the next dataset's legs will start there.
    /// </para>
    /// <para>
    /// <b>More than one figure per pair is allowed, and is the answer to a different machine.</b>
    /// If a hosted runner reproduces something legitimately outside <see cref="Tolerance"/> of every
    /// figure here, the fix is another entry naming that machine — not a wider window. Widening
    /// costs every dataset its resolution to settle one runner; a second figure costs nothing and
    /// says plainly that two machines disagree.
    /// </para>
    /// </remarks>
    private static readonly Reproduction[] Reproductions =
    [
        new(
            "scifact",
            BeirProtocol.Parity,
            [0.64593],
            "Both separators, and they agree exactly — Phase 3.13 fixed the tokenizer defect that " +
            "made the newline leg read 0.64907. 300 of 1,109 queries judged. Windows 11, .NET 10, " +
            "CPU ONNX Runtime; re-measured 2026-07-31."),
        new(
            "scifact",
            BeirProtocol.Real,
            [0.67742],
            "20,155 chunks over 5,183 of 5,183 documents, max 25 from one, pooled on all 1,109 " +
            "queries — a count over every query in queries.jsonl; since Phase 3.15 cut retrieval " +
            "to the judged set, a re-run pools on at most the 300 judged and the nDCG is unchanged " +
            "by construction. Re-measured 2026-07-31 on the same machine, 643.1 s, after Phase 3.16 taught " +
            "RecursiveChunkingStrategy to pack split parts towards MaxChunkSize — the packing " +
            "lifted this leg from 3.12's 0.65589 (56,707 chunks, max 221) to 0.67742, +0.03148 " +
            "against a parity leg the fix did not move."),
        new(
            "fiqa",
            BeirProtocol.Parity,
            [0.37086],
            "One separator — FiQA titles none of its 57,638 documents. Measured 2026-07-31, 1 h " +
            "11 m for 64,247 embeddings, retrieving for all 6,648 queries; since Phase 3.15 cut " +
            "retrieval to the 648 judged, a re-run embeds ~6,000 fewer query texts and the nDCG " +
            "is unchanged by construction. GATED: BeirRunBudget keeps this case behind " +
            "RAGNET_BEIR_LONG_RUNS, so nothing re-checks this figure unless somebody asks for it."),
        new(
            "fiqa",
            BeirProtocol.Real,
            [0.35569],
            "121,236 units over 57,600 of 57,638 documents, max 41 from one — 38 corpus entries " +
            "have an empty title and an empty text and so yield no chunks, one of them (117276) " +
            "judged relevant, so this leg indexes 38 fewer documents than parity. Pooled two or " +
            "more units on all 648 judged queries. First measured 2026-08-02 (Phase 3.15), same " +
            "machine: real leg 3,587.5 s (59.8 min), whole test 1 h 4 m — the derived ~1.5-2 h " +
            "estimate it replaces was conservative. Recall@10 0.42235, MRR@10 0.42596; the " +
            "parity leg in the same run reproduced 0.37086 exactly, so the delta is -0.01517."),
        new(
            "arguana",
            BeirProtocol.Parity,
            [0.50432],
            "Both separators, which agree. All 1,406 queries judged. Measured 2026-07-31; ~50 s " +
            "warm, and one of the two parity legs the nightly still runs unasked."),
        new(
            "arguana",
            BeirProtocol.Real,
            [0.47559],
            "24,003 chunks over 8,674 documents, max 16 from one, pooled on all 1,406 queries. " +
            "Re-measured 2026-07-31, 667.1 s, under Phase 3.16's packing chunker. Its delta of " +
            "-0.02873 against the unmoved parity leg is still the headline number and is pinned by " +
            "pinning both legs — packing recovered about 63% of 3.12's -0.07839 (0.42594, 82,618 " +
            "chunks, max 285), which is what design §6 predicted if fragmentation was the cause."),
        new(
            "scifact",
            BeirProtocol.HybridBm25,
            [0.69913],
            "Parity corpus, dense fused with InMemoryBm25Index via RRF — this repository's own " +
            "reproduction, comparable to the dense anchor and to no published BM25 or hybrid " +
            "figure. Recall@10 0.83933, MRR@10 0.65676. Measured in Phase 3.15 (2026-08-01/02), " +
            "Windows 11, .NET 10, CPU ONNX Runtime."),
        new(
            "scifact",
            BeirProtocol.Hyde,
            [0.70001],
            "Parity corpus searched with the mean of 3 cached gpt-4o-mini@t0.8 hypotheticals — " +
            "this repository's own reproduction, comparable to nothing published. Recall@10 " +
            "0.85033, MRR@10 0.65563. Measured in Phase 3.15 (2026-08-01/02), Windows 11, .NET " +
            "10, CPU ONNX Runtime."),
        new(
            "scifact",
            BeirProtocol.Reranked,
            [0.68442],
            "Dense top-k rescored by cross-encoder/ms-marco-MiniLM-L6-v2 — this repository's own " +
            "reproduction, comparable to nothing published. Recall@10 0.78667, MRR@10 0.65789. " +
            "Measured in Phase 3.15 (2026-08-01/02), Windows 11, .NET 10, CPU ONNX Runtime, " +
            "after commit a912187 replaced the whitespace word-lookup tokenizer with WordPiece. " +
            "The pre-fix run measured 0.56693 — history, not a figure to reproduce: a run " +
            "landing there again means the tokenizer regressed."),
        new(
            "fiqa",
            BeirProtocol.HybridBm25,
            [0.35665],
            "Parity corpus, dense fused with InMemoryBm25Index via RRF — this repository's own " +
            "reproduction, comparable to the dense anchor and to no published BM25 or hybrid " +
            "figure. Recall@10 0.43951, MRR@10 0.42914. Measured in Phase 3.15 (2026-08-01/02), " +
            "Windows 11, .NET 10, CPU ONNX Runtime."),
        new(
            "fiqa",
            BeirProtocol.Hyde,
            [0.36543],
            "Parity corpus searched with the mean of 3 cached gpt-4o-mini@t0.8 hypotheticals — " +
            "this repository's own reproduction, comparable to nothing published. Recall@10 " +
            "0.44738, MRR@10 0.43124. Measured in Phase 3.15 (2026-08-01/02), Windows 11, .NET " +
            "10, CPU ONNX Runtime."),
        new(
            "fiqa",
            BeirProtocol.Reranked,
            [0.38458],
            "Dense top-k rescored by cross-encoder/ms-marco-MiniLM-L6-v2 — this repository's own " +
            "reproduction, comparable to nothing published, and the ablation table's only " +
            "reranker lift. Recall@10 0.44295, MRR@10 0.46744. Measured in Phase 3.15 " +
            "(2026-08-01/02), Windows 11, .NET 10, CPU ONNX Runtime, after commit a912187's " +
            "WordPiece fix. The pre-fix run measured 0.34085 — history, not a figure to " +
            "reproduce."),
        new(
            "arguana",
            BeirProtocol.HybridBm25,
            [0.51173],
            "Parity corpus, dense fused with InMemoryBm25Index via RRF — this repository's own " +
            "reproduction, comparable to the dense anchor and to no published BM25 or hybrid " +
            "figure. Recall@10 0.80228, MRR@10 0.42141. Measured in Phase 3.15 (2026-08-01/02), " +
            "Windows 11, .NET 10, CPU ONNX Runtime."),
        new(
            "arguana",
            BeirProtocol.Hyde,
            [0.50293],
            "Parity corpus searched with the mean of 3 cached gpt-4o-mini@t0.8 hypotheticals — " +
            "this repository's own reproduction, comparable to nothing published; the design's " +
            "negative control, and it held. Recall@10 0.79516, MRR@10 0.41258. Measured in " +
            "Phase 3.15 (2026-08-01/02), Windows 11, .NET 10, CPU ONNX Runtime."),
        new(
            "arguana",
            BeirProtocol.Reranked,
            [0.47917],
            "Dense top-k rescored by cross-encoder/ms-marco-MiniLM-L6-v2 — this repository's own " +
            "reproduction, comparable to nothing published. Recall@10 0.79374, MRR@10 0.38188. " +
            "Measured in Phase 3.15 (2026-08-01/02), Windows 11, .NET 10, CPU ONNX Runtime, " +
            "after commit a912187's WordPiece fix. The pre-fix run measured 0.41806 — history, " +
            "not a figure to reproduce."),
    ];

    /// <summary>
    /// Asserts one measurement reproduced what this repository last recorded for that case.
    /// </summary>
    /// <param name="datasetName">The BEIR dataset name, as it appears in the theory data.</param>
    /// <param name="protocol">Which protocol produced <paramref name="measuredNdcgAt10"/>.</param>
    /// <param name="measuredNdcgAt10">What the run just reported.</param>
    /// <param name="output">Where the "nothing recorded yet" note is written.</param>
    /// <exception cref="InvalidOperationException">Nothing is recorded for that pair at all.</exception>
    public static void AssertReproduces(
        string datasetName,
        BeirProtocol protocol,
        double measuredNdcgAt10,
        ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var recorded = Find(datasetName, protocol);
        if (recorded.NdcgAt10.Count == 0)
        {
            output.WriteLine(NothingRecordedYet(recorded, measuredNdcgAt10));
            return;
        }

        Assert.True(
            Reproduces(recorded, measuredNdcgAt10),
            Explain(recorded, measuredNdcgAt10));
    }

    /// <summary>
    /// Provokes the table lookup for one case and compares nothing.
    /// </summary>
    /// <param name="datasetName">The BEIR dataset name.</param>
    /// <param name="protocol">Which protocol the case measures under.</param>
    /// <exception cref="InvalidOperationException">Nothing is recorded for that pair at all.</exception>
    /// <remarks>
    /// So <see cref="BeirReproductionTests"/> can assert that every described dataset has an entry
    /// without inventing an nDCG to compare against — a plausible figure there would make that test
    /// quietly depend on which numbers the table happens to hold, and an implausible one would fail
    /// it.
    /// </remarks>
    public static void RequireRecordedCase(string datasetName, BeirProtocol protocol) =>
        _ = Find(datasetName, protocol);

    /// <summary>Reports whether the measurement lands within tolerance of any recorded figure.</summary>
    private static bool Reproduces(Reproduction recorded, double measured)
    {
        for (var i = 0; i < recorded.NdcgAt10.Count; i++)
        {
            if (Math.Abs(measured - recorded.NdcgAt10[i]) <= Tolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds the recorded reproduction for one dataset under one protocol.</summary>
    /// <exception cref="InvalidOperationException">The pair is not in the table.</exception>
    private static Reproduction Find(string datasetName, BeirProtocol protocol)
    {
        foreach (var reproduction in Reproductions)
        {
            if (string.Equals(reproduction.Dataset, datasetName, StringComparison.Ordinal)
                && reproduction.Protocol == protocol)
            {
                return reproduction;
            }
        }

        throw new InvalidOperationException(
            $"No reproduction is recorded for dataset '{datasetName}' under the {protocol} " +
            $"protocol. A dataset was added to {nameof(BeirDatasetDescriptor)}.All and its number " +
            $"is pinned by nothing: the published band is ±{BeirParityTarget.DefaultTolerance:F2} " +
            "and the real run's envelope is half to one-and-a-half times parity, so a regression " +
            $"of 0.015 would pass both green. Run it and add it to {nameof(BeirReproduction)}, " +
            "recording an empty figure list if it is a case nobody has run to completion.");
    }

    /// <summary>The line a case with no recorded figure prints instead of asserting.</summary>
    private static string NothingRecordedYet(Reproduction recorded, double measured) =>
        FormattableString.Invariant($"""
            NO REPRODUCTION RECORDED for {recorded.Dataset} under {recorded.Protocol}, so nothing was
            checked. This run measured nDCG@10 = {measured:F5}.
            Recorded instead: {recorded.Provenance}
            If this run finished, it is the figure — put it in {nameof(BeirReproduction)} with the
            machine and the date, and the next run will be checked against it.
            """);

    /// <summary>
    /// The message a drifted measurement leaves behind, and the reason it insists on being read
    /// before anything is edited.
    /// </summary>
    /// <remarks>
    /// The first instinct on a red band in this project has historically been to widen it, and the
    /// parity failure message already says not to. This one has an extra trap to name: the number it
    /// guards is <i>ours</i>, so "the published figure moved" is never the explanation, and the only
    /// two honest resolutions are a fixed regression or a second recorded machine.
    /// </remarks>
    private static string Explain(Reproduction recorded, double measured) =>
        FormattableString.Invariant($"""
            {recorded.Dataset} {recorded.Protocol} run measured nDCG@10 = {measured:F5}, which reproduces
            none of the figures recorded for it: {Format(recorded.NdcgAt10)} (±{Tolerance:F3}).
            THIS IS NOT A PARITY FAILURE. Nothing published is involved — the figures above are this
            repository's own, recorded as: {recorded.Provenance}
            The point of the check is that the ±{BeirParityTarget.DefaultTolerance:F2} published band and the
            real run's 0.5x-1.5x envelope are both wider than most defects: a cut-then-pool mutation of
            DocumentRanking moves this number by roughly 0.016-0.020 and passes both of them green.
            Two honest resolutions, and no third:
              1. Something regressed. Find it. The delta between the two protocols in this run is the
                 first thing to look at, then the run's own counters — units indexed, documents that
                 contributed nothing, and how many queries pooled.
              2. This is a different machine and the difference is legitimate. Add a SECOND figure to
                 this case naming the machine. Do NOT widen {nameof(Tolerance)}: that spends every
                 dataset's resolution to settle one runner.
            """);

    /// <summary>Renders the recorded figures for a message.</summary>
    private static string Format(IReadOnlyList<double> figures)
    {
        var rendered = new string[figures.Count];
        for (var i = 0; i < figures.Count; i++)
        {
            rendered[i] = FormattableString.Invariant($"{figures[i]:F5}");
        }

        return string.Join(", ", rendered);
    }

    /// <summary>What one case measured, where, and when.</summary>
    /// <param name="Dataset">The BEIR dataset name.</param>
    /// <param name="Protocol">The protocol measured.</param>
    /// <param name="NdcgAt10">
    /// Every nDCG@10 this case has legitimately reported, one per machine that has run it. Empty
    /// means the case has never been run to completion, which <see cref="AssertReproduces"/> treats
    /// as "print it and check nothing" rather than as a failure.
    /// </param>
    /// <param name="Provenance">
    /// Where and when the figures came from, in prose. Not decoration: a reproduction check whose
    /// numbers have no machine attached is indistinguishable from a parity band, which is the one
    /// thing this must not be mistaken for.
    /// </param>
    private sealed record Reproduction(
        string Dataset,
        BeirProtocol Protocol,
        IReadOnlyList<double> NdcgAt10,
        string Provenance);
}
