using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Guards the budget table itself, in the fast tier, on every push.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not gated on provisioning.</b> Every other test in this project skips without
/// <c>RAGNET_ONNX_EMBED_MODEL</c> and its siblings, which means a defect in
/// <see cref="BeirRunBudget"/> would first be seen by the nightly — at 03:17 UTC, in the one job
/// whose budget the table exists to protect. These need no model, no corpus and no environment at
/// all, so they run in <c>ci.yml</c>'s fast tier on every push and fail there instead.
/// </para>
/// <para>
/// This is the same shape as the guard <c>BeirHarness.LoadAsync</c> applies to the corpus counts:
/// the cheap assertion that makes the expensive run's failure diagnosable, made before anything
/// expensive happens.
/// </para>
/// </remarks>
public sealed class BeirRunBudgetTests
{
    [Fact]
    public void EveryDescribedDatasetHasARecordedCostUnderBothProtocols()
    {
        // BeirRunBudget.Find throws on a pair it has no measurement for, which is the behaviour that
        // stops a fourth dataset from silently defaulting into — or out of — the nightly. But that
        // throw only fires when the case actually runs, and the cases that run are exactly the ones
        // gated behind provisioning. So the throw is provoked here, where nothing is gated.
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            _ = BeirRunBudget.IsGatedOff(descriptor.Name, BeirProtocol.Parity, out _);
            _ = BeirRunBudget.IsGatedOff(descriptor.Name, BeirProtocol.Real, out _);
        }
    }

    [Fact]
    public void TheNightlyStillMeasuresParityOnAtLeastTwoDatasets()
    {
        // The other direction, and the one that matters more. Gating is easy to widen — the next
        // case that runs long is one table edit from being opt-in too — and a budget table whose
        // every row said "opt-in" would produce a fast, green, entirely meaningless nightly. That is
        // precisely the failure this workflow was fixed to stop: a job that passes having measured
        // nothing. Two is the number that survives today; raising this is fine, lowering it is the
        // thing to argue about in review rather than in a commit nobody reads.
        var measured = 0;
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            if (!BeirRunBudget.IsGatedOff(descriptor.Name, BeirProtocol.Parity, out _))
            {
                measured++;
            }
        }

        Assert.True(
            measured >= 2,
            $"Only {measured} dataset(s) still run their PARITY measurement without " +
            $"{BeirRunBudget.OptInVariable}. Parity is the only protocol whose number can be checked " +
            "against a published figure, so it is the whole regression signal the nightly carries. " +
            "Gating it down to one dataset — or none — leaves a job that finishes quickly, passes, " +
            "and watches nothing.");
    }
}
