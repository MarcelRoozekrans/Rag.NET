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
    public void EveryApplicablePairHasARecordedCost_AndNoInapplicablePairHasOne()
    {
        // BeirRunBudget.Find throws on a pair it has no measurement for, which is the behaviour that
        // stops a fourth dataset from silently defaulting into — or out of — the nightly. But that
        // throw only fires when the case actually runs, and the cases that run are exactly the ones
        // gated behind provisioning. So the throw is provoked here, where nothing is gated. Every
        // protocol, not just the two chunking legs: since Phase 3.15 the ablation cells gate through
        // the same table, so a fourth dataset owes those three measurements too before its cells can
        // skip with an honest cost.
        //
        // And the other direction, which the requirement alone does not cover. A descriptor can now
        // declare a protocol inapplicable, and a budget cell surviving that declaration is a
        // contradiction the table cannot detect on its own: Find is only ever consulted for pairs
        // somebody runs, so a cell for a pair nobody can run is read by nothing and deleted by
        // nobody. It also does not look stale — a measured-looking string beside FitsTheNightly
        // reads exactly like a measurement somebody took, which is how this project has previously
        // ended up with guards that were green over nothing. Required where applicable, refused
        // where not; either half alone is not a guard.
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            foreach (var protocol in Enum.GetValues<BeirProtocol>())
            {
                if (descriptor.Supports(protocol))
                {
                    _ = BeirRunBudget.IsGatedOff(descriptor.Name, protocol, out _);
                    continue;
                }

                Assert.False(
                    BeirRunBudget.HasCost(descriptor.Name, protocol),
                    $"{descriptor.Name} declares {protocol} inapplicable but still carries a budget " +
                    "cell. One of the two is wrong, and a stale cell looks exactly like a measurement.");
            }
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
        //
        // FitsTheNightly, never IsGatedOff. The latter consults RAGNET_BEIR_LONG_RUNS, so a
        // developer who sets it to run a measurement would turn this test into an assertion that
        // three is at least two — green whatever the table says, on the one machine most likely to
        // be editing the table.
        // Supports before FitsTheNightly, and not as a courtesy. FitsTheNightly goes through Find,
        // which throws on a pair the table holds no cell for — and since the table became
        // bidirectional it correctly holds no Parity cell for a dataset that declares Parity
        // inapplicable, which MultiHop-RAG does. Asking the table about that pair anyway turns this
        // guard into an InvalidOperationException complaining that somebody forgot to measure
        // something nobody can measure: a true statement about the wrong thing, in place of the
        // count this test exists to assert.
        var measured = 0;
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            if (descriptor.Supports(BeirProtocol.Parity)
                && BeirRunBudget.FitsTheNightly(descriptor.Name, BeirProtocol.Parity))
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
