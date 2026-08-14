using System.Reflection;
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

    [Fact]
    public void EveryCellsPrintedFilterCanSelectATest()
    {
        // The third thing a skip message promises, after what did not run and what it costs: the
        // command that runs it. That promise went unkept for a release. BeirProtocol.GraphRag's
        // filter conjoined DisplayName~GraphRag with DisplayName~multihop-rag, and the case it
        // names is a [Fact] over one pinned slice rather than a theory over datasets — so its
        // display name holds no dataset, the conjunction selected nothing, and `dotnet test`
        // reported "No test matches the given testcase filter" and EXITED 0. A green run for a case
        // that never ran is the exact failure this whole project keeps removing, and it was pasted
        // out of this repository's own instructions.
        //
        // Nothing checked it, which is why it drifted. This does, by reflection over this assembly
        // rather than against a list of display names — a hardcoded list would need editing by the
        // same rename that breaks the filter, which moves the drift instead of catching it.
        //
        // Two properties, and they fail for different reasons. A discriminator matching no test
        // method's name at all is a rename or a deletion. A discriminator that matches, on a filter
        // that also conjoins the dataset, needs a method taking a `datasetName` parameter: that
        // parameter is the ONLY thing that puts a dataset into an xUnit display name, so without it
        // the second conjunct subtracts everything the first found. That second property is the
        // GraphRag defect exactly.
        //
        // What this deliberately does not assert is that the theory's data actually contains this
        // dataset — that pairing is what the applicability guard above is for, and reaching into
        // MemberData here would duplicate it badly.
        var tests = TestMethods();
        var failures = new List<string>();

        foreach (var (dataset, protocol, filter) in BeirRunBudget.PrintedFilters())
        {
            var failure = WhyNothingCanMatch(filter, dataset, tests);
            if (failure is not null)
            {
                failures.Add($"{dataset} / {protocol} prints --filter \"{filter}\", and {failure}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of the budget table's cells print a --filter that cannot select a " +
            "test. vstest answers an empty selection with \"No test matches the given testcase " +
            "filter\" and EXIT CODE 0, so a reader who follows the skip message sees a successful " +
            "run and records a pass for a measurement that never happened." + Environment.NewLine +
            "  - " + string.Join(Environment.NewLine + "  - ", failures));
    }

    /// <summary>
    /// Says why no test in this assembly can match one filter, or <see langword="null"/> when one
    /// can.
    /// </summary>
    private static string? WhyNothingCanMatch(
        string filter, string dataset, IReadOnlyList<MethodInfo> tests)
    {
        var conjuncts = filter.Split('&');
        var discriminators = new List<string>(conjuncts.Length);
        var conjoinsTheDataset = false;

        foreach (var conjunct in conjuncts)
        {
            var value = ValueOf(conjunct);
            if (string.Equals(value, dataset, StringComparison.Ordinal))
            {
                conjoinsTheDataset = true;
                continue;
            }

            discriminators.Add(value);
        }

        var named = NamedBy(tests, discriminators);
        if (named.Count == 0)
        {
            return NothingIsNamed(discriminators, tests.Count);
        }

        if (conjoinsTheDataset && !AnyTakesDatasetName(named))
        {
            return NothingCarriesTheDataset(dataset, named);
        }

        return null;
    }

    /// <summary>The right-hand side of one <c>Property~Value</c> conjunct.</summary>
    private static string ValueOf(string conjunct)
    {
        var separator = conjunct.IndexOf('~', StringComparison.Ordinal);

        return separator < 0 ? conjunct : conjunct[(separator + 1)..];
    }

    /// <summary>
    /// The test methods whose own name carries every discriminator — which is all a filter can
    /// select on before theory arguments are rendered into the display name.
    /// </summary>
    private static List<MethodInfo> NamedBy(
        IReadOnlyList<MethodInfo> tests, IReadOnlyList<string> discriminators)
    {
        var named = new List<MethodInfo>();
        for (var i = 0; i < tests.Count; i++)
        {
            var carries = true;
            for (var j = 0; j < discriminators.Count && carries; j++)
            {
                carries = Identity(tests[i]).Contains(discriminators[j], StringComparison.Ordinal);
            }

            if (carries)
            {
                named.Add(tests[i]);
            }
        }

        return named;
    }

    /// <summary>Reports whether any candidate takes the parameter that names a dataset.</summary>
    private static bool AnyTakesDatasetName(IReadOnlyList<MethodInfo> named)
    {
        for (var i = 0; i < named.Count; i++)
        {
            foreach (var parameter in named[i].GetParameters())
            {
                if (string.Equals(parameter.Name, "datasetName", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The message for a discriminator that names nothing: a rename or a deletion.</summary>
    private static string NothingIsNamed(IReadOnlyList<string> discriminators, int scanned) =>
        $"no test method among the {scanned} in this assembly carries " +
        $"\"{string.Join("\" and \"", discriminators)}\" in its name. The discriminator names a " +
        "class or a method that has been renamed or removed, so the filter selects nothing at all.";

    /// <summary>The message for the defect this guard was written for.</summary>
    private static string NothingCarriesTheDataset(string dataset, IReadOnlyList<MethodInfo> named) =>
        $"the DisplayName~{dataset} conjunct excludes every test the discriminator found. " +
        $"{named.Count} test method(s) match by name — {Names(named)} — and not one of them takes " +
        "a `datasetName` parameter. That parameter is the only thing that puts a dataset into an " +
        "xUnit display name, so a [Fact] can never satisfy this conjunct and the conjunction " +
        "matches zero tests. Select this case by identity (FullyQualifiedName~<its class>) " +
        "instead, or give the case a theory that takes the dataset.";

    /// <summary>The candidates, named the way the filter would have had to name them.</summary>
    private static string Names(IReadOnlyList<MethodInfo> named)
    {
        var names = new List<string>(named.Count);
        for (var i = 0; i < named.Count; i++)
        {
            names.Add(Identity(named[i]));
        }

        return string.Join(", ", names);
    }

    /// <summary>How much of a test's display name exists before its arguments are rendered.</summary>
    private static string Identity(MethodInfo test) =>
        test.DeclaringType!.FullName + "." + test.Name;

    /// <summary>Every <c>[Fact]</c> and <c>[Theory]</c> method in the integration-test assembly.</summary>
    private static List<MethodInfo> TestMethods()
    {
        var tests = new List<MethodInfo>();
        foreach (var type in typeof(BeirRunBudget).Assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            {
                if (method.IsDefined(typeof(FactAttribute), inherit: true))
                {
                    tests.Add(method);
                }
            }
        }

        return tests;
    }
}
