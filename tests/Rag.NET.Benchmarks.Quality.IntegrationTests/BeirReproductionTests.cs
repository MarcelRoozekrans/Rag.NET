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
/// nine hours and four of the six are opt-in, so a defect in the check would be found by almost
/// nothing. Everything here needs no model, no corpus and no environment.
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
    public void EveryDescribedDatasetHasARecordedReproductionUnderBothProtocols()
    {
        // BeirReproduction.Find throws on a pair it holds nothing for, which is what stops a fourth
        // dataset from joining the suite pinned by nothing but a ±0.02 published band. That throw
        // only fires when the case runs, and four of the six cases are gated behind
        // RAGNET_BEIR_LONG_RUNS — so it is provoked here, where nothing is gated.
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            BeirReproduction.RequireRecordedCase(descriptor.Name, BeirProtocol.Parity);
            BeirReproduction.RequireRecordedCase(descriptor.Name, BeirProtocol.Real);
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
    public void ACaseNobodyHasRunToCompletionChecksNothingAndPrintsWhatItSaw()
    {
        // FiQA's real leg is estimated at 8-9 hours and has never finished. Failing that run for
        // not matching a figure nobody could have recorded would be a test that punishes the person
        // who finally paid for it; it prints instead, naming what to write down.
        BeirReproduction.AssertReproduces("fiqa", BeirProtocol.Real, 0.12345, _output);
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
