using Rag.NET.Benchmarks.Quality;
using Xunit;
using Xunit.Sdk;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Guards the reproduction table itself, in the fast tier, on every push.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not gated on provisioning</b>, for the reason <see cref="BeirRunBudgetTests"/>
/// gives about the budget table and one more that is specific to this one: the measurements that
/// exercise <see cref="BeirReproduction.AssertReproduces"/> for real cost between 50 seconds and
/// over an hour and all but the nightly's two parity datasets are opt-in, so a defect in the
/// check would be found by almost nothing. Everything here needs no model, no corpus and no
/// environment.
/// </para>
/// <para>
/// It also means the mechanism is tested where the measurements are not: that a drift fails, that a
/// case nobody has run yet does not, and that a dataset without an entry throws.
/// </para>
/// </remarks>
public sealed class BeirReproductionTests
{
    private readonly ITestOutputHelper _output;

    public BeirReproductionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void EveryDescribedDatasetHasARecordedReproductionUnderEveryProtocol()
    {
        // BeirReproduction.Find throws on a pair it holds nothing for, which is what stops a fourth
        // dataset from joining the suite pinned by nothing but a ±0.02 published band. That throw
        // only fires when the case runs, and most cases are gated behind RAGNET_BEIR_LONG_RUNS —
        // so it is provoked here, where nothing is gated. Every protocol, not just the two
        // chunking legs: since Phase 3.15 pinned the ablation cells, a fourth dataset owes those
        // three figures too (or an entry recording that nobody has run them).
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            foreach (var protocol in Enum.GetValues<BeirProtocol>())
            {
                BeirReproduction.RequireRecordedCase(descriptor.Name, protocol);
            }
        }
    }

    [Fact]
    public void ADriftOfOneHundredthFails_WhichIsTheWholeReasonThisExistsBesideTheParityBand()
    {
        // 0.01 is the size of drift the project could not see before this table: inside SciFact's
        // ±0.02 published band, inside the real run's 0.5x-1.5x envelope by a wide margin, and
        // bigger than either of the two mutations the Phase 3.12 review demonstrated passing green.
        var exception = Assert.ThrowsAny<XunitException>(
            () => BeirReproduction.AssertReproduces(
                "scifact", BeirProtocol.Parity, 0.64593 - 0.01, _output));

        Assert.Contains("THIS IS NOT A PARITY FAILURE", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Do NOT widen", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.64593)]
    [InlineData(0.64993)]
    [InlineData(0.64193)]
    public void AMeasurementInsideTheWindowReproduces_SoTheCheckIsNotAnExactMatch(double measured)
    {
        // Inclusive at both edges, and not an exact match on purpose: ONNX Runtime dispatches its
        // kernels on the available instruction set, so another CPU can differ in the last bits of a
        // vector and resolve a near-tie the other way. A test that demanded the fifth decimal
        // everywhere would go red for a reason nobody could act on, and those get deleted.
        BeirReproduction.AssertReproduces("scifact", BeirProtocol.Parity, measured, _output);
    }

    [Fact]
    public void TheFormerlyUnrunFiqaRealCaseNowAssertsLikeEveryOtherCase()
    {
        // Until Phase 3.15 this case held an empty figure list and AssertReproduces printed what
        // the run saw instead of asserting — the state a case sits in while nobody has paid for
        // it, and the state the next dataset's legs will start in. FiQA's real leg was measured
        // on 2026-08-02 (0.35569), so a drift the empty entry would have waved through now fails
        // like any other case.
        var exception = Assert.ThrowsAny<XunitException>(
            () => BeirReproduction.AssertReproduces(
                "fiqa", BeirProtocol.Real, 0.35569 - 0.01, _output));

        Assert.Contains("THIS IS NOT A PARITY FAILURE", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fiqa", BeirProtocol.Real, 0.35569)]
    [InlineData("scifact", BeirProtocol.HybridBm25, 0.69913)]
    [InlineData("scifact", BeirProtocol.Hyde, 0.70001)]
    [InlineData("scifact", BeirProtocol.Reranked, 0.68442)]
    [InlineData("fiqa", BeirProtocol.HybridBm25, 0.35665)]
    [InlineData("fiqa", BeirProtocol.Hyde, 0.36543)]
    [InlineData("fiqa", BeirProtocol.Reranked, 0.38458)]
    [InlineData("arguana", BeirProtocol.HybridBm25, 0.51173)]
    [InlineData("arguana", BeirProtocol.Hyde, 0.50293)]
    [InlineData("arguana", BeirProtocol.Reranked, 0.47917)]
    public void TheFiguresThePagePublishesAreTheFiguresTheTableHolds(
        string datasetName, BeirProtocol protocol, double published)
    {
        // The nine ablation cells and FiQA's real leg run only behind RAGNET_BEIR_LONG_RUNS, so a
        // mutated table entry would be caught by nothing until somebody paid for the run. These
        // are the figures docs/reference/retrieval-quality.md publishes, restated so the fast
        // tier fails when either copy moves without the other — the same double entry the parity
        // theories above give SciFact's 0.64593.
        BeirReproduction.AssertReproduces(datasetName, protocol, published, _output);
    }

    [Fact]
    public void TheControlRowsRecordedFiguresAreTheParityFiguresItReproduced()
    {
        // Phase 3.14's control row — the parity rankings scored from a read-back TREC run file —
        // reproduced both measured parity figures exactly, which is the acceptance gate for the
        // whole library comparison: the boundary added and removed nothing. Its cases run only
        // behind RAGNET_BEIR_LONG_RUNS, so a mutated Comparison entry would otherwise be caught
        // by nothing until somebody paid for a measurement; restated here, the fast tier fails
        // when either copy moves without the other. FiQA's leg has never been run and its entry
        // is empty — it is deliberately absent from this double entry rather than invented.
        BeirReproduction.AssertReproduces("scifact", BeirProtocol.Comparison, 0.64593, _output);
        BeirReproduction.AssertReproduces("arguana", BeirProtocol.Comparison, 0.50432, _output);
    }

    [Fact]
    public void AnUndescribedDatasetThrowsRatherThanPassing()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => BeirReproduction.AssertReproduces(
                "trec-covid", BeirProtocol.Parity, 0.5, _output));

        Assert.Contains("No reproduction is recorded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowIsTighterThanTheBandItComplementsAndThanTheDefectItMustCatch()
    {
        // Both ends of the choice, asserted rather than only argued in a remark. Above 0.0158 it
        // stops catching the cut-then-pool mutation that started this; at or above the published
        // band it stops adding anything the parity test did not already have.
        Assert.True(BeirReproduction.Tolerance < 0.0158);
        Assert.True(BeirReproduction.Tolerance < BeirParityTarget.DefaultTolerance);
    }
}
