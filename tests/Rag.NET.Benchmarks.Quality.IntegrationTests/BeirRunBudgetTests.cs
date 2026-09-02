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
    public void AnAbsentOptIn_GatesEveryDatasetOff()
    {
        // The default, and the one the nightly depends on: no variable means no expensive run.
        Assert.False(BeirRunBudget.IsOptedInFor(null, "scifact"));
        Assert.False(BeirRunBudget.IsOptedInFor(string.Empty, "scifact"));
        Assert.False(BeirRunBudget.IsOptedInFor("   ", "scifact"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void AnOffValue_GatesEveryDatasetOff(string value)
    {
        // "RAGNET_BEIR_LONG_RUNS=0 reads to every human as off", and did before this change.
        Assert.False(BeirRunBudget.IsOptedInFor(value, "scifact"));
        Assert.False(BeirRunBudget.IsOptedInFor(value, "trec-covid"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void AWholesaleOptIn_StillOptsInEveryDataset(string value)
    {
        // Backwards compatibility, asserted rather than assumed: every documented invocation in
        // this repository is =1, and all thirteen of them must keep meaning what they meant.
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            Assert.True(
                BeirRunBudget.IsOptedInFor(value, descriptor.Name),
                $"{value} must still opt in {descriptor.Name}, as it did before the gate learned " +
                "about dataset names.");
        }
    }

    [Fact]
    public void ADatasetName_OptsInThatDatasetAlone()
    {
        // The whole point. Measuring one cell must not ungate the other three, because the
        // expensive ones are not the one being measured: TREC-COVID's Real leg has never been
        // embedded, so a case that ungates it chunks a corpus 33x SciFact's before failing.
        Assert.True(BeirRunBudget.IsOptedInFor("scifact", "scifact"));
        Assert.False(BeirRunBudget.IsOptedInFor("scifact", "fiqa"));
        Assert.False(BeirRunBudget.IsOptedInFor("scifact", "arguana"));
        Assert.False(BeirRunBudget.IsOptedInFor("scifact", "trec-covid"));
    }

    [Fact]
    public void AListOfDatasetNames_OptsInEachOfThemAndNothingElse()
    {
        Assert.True(BeirRunBudget.IsOptedInFor("scifact,fiqa", "scifact"));
        Assert.True(BeirRunBudget.IsOptedInFor("scifact,fiqa", "fiqa"));
        Assert.False(BeirRunBudget.IsOptedInFor("scifact,fiqa", "arguana"));
    }

    [Fact]
    public void SurroundingWhitespaceAndCasing_DoNotChangeWhichDatasetsAreSelected()
    {
        // A shell heredoc and a copied-out command both produce these, and a gate that silently
        // read " scifact" as an unknown name would throw on a value the author got right.
        Assert.True(BeirRunBudget.IsOptedInFor(" scifact , fiqa ", "fiqa"));
        Assert.True(BeirRunBudget.IsOptedInFor("SciFact", "scifact"));
        Assert.False(BeirRunBudget.IsOptedInFor(" scifact , fiqa ", "trec-covid"));
    }

    [Fact]
    public void AnUnknownName_ThrowsRatherThanWideningToEveryDataset()
    {
        // The failure mode this gate exists to remove, in its most likely form: a typo. Widening
        // to every dataset on an unrecognised value is what the old rule did -- "anything else
        // present is on" -- and it is how a run nobody scheduled cost 6 h 18 m. A typo must stop
        // the run, not silently buy the largest one available.
        var thrown = Assert.Throws<InvalidOperationException>(
            () => BeirRunBudget.IsOptedInFor("scifct", "scifact"));

        Assert.Contains("scifct", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("scifact", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(BeirRunBudget.OptInVariable, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKnownNameBesideAnUnknownOne_ThrowsRatherThanMeasuringThePartItUnderstood()
    {
        // Partial credit is the wrong answer here. "scifact,fiqua" reads as two datasets to its
        // author and would measure one, reporting a green run that answered half the question --
        // and the half it dropped is invisible in a passing summary.
        var thrown = Assert.Throws<InvalidOperationException>(
            () => BeirRunBudget.IsOptedInFor("scifact,fiqua", "scifact"));

        Assert.Contains("fiqua", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSkipMessagesCommand_OptsInOnlyTheCasesOwnDataset()
    {
        // The skip message is where a reader learns how to run the case, so it is also where they
        // learn what the variable means. Printing =1 teaches the reading this change exists to
        // remove: it ungates all four datasets to measure one, which is how FiQA's RealReranked
        // cell ran 6 h 18 m unscheduled. The command a message prints must be the safe one.
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            foreach (var protocol in Enum.GetValues<BeirProtocol>())
            {
                if (!descriptor.Supports(protocol)
                    || BeirRunBudget.FitsTheNightly(descriptor.Name, protocol))
                {
                    continue;
                }

                var message = BeirRunBudget.ExplainFor(descriptor.Name, protocol);

                Assert.Contains(
                    $"{BeirRunBudget.OptInVariable}={descriptor.Name}",
                    message,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    $"{BeirRunBudget.OptInVariable}=1",
                    message,
                    StringComparison.Ordinal);
            }
        }
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
        // Both properties are per-ALTERNATIVE, and that distinction is not decoration. vstest's
        // filter grammar has two operators over the subset this table emits: `&` conjoins within an
        // alternative and `|` separates alternatives, with `|` binding looser. A guard that knew
        // only `&` read `A|B` as the single discriminator "A|B", found no method carrying a pipe in
        // its name, and failed a correct filter while reporting a rename that had not happened — a
        // false positive, which costs more than the defect it imitates, because the person who hits
        // it fixes the guard rather than the code.
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
            $"{failures.Count} of the budget table's cells print a --filter that cannot select " +
            "every test it names. vstest answers an empty selection with \"No test matches the " +
            "given testcase filter\" and EXIT CODE 0, so a reader who follows the skip message sees " +
            "a successful run and records a pass for a measurement that never happened — and an " +
            "alternation with a dead branch does the same thing more quietly, selecting a subset " +
            "under an exit code that says everything the cell prices ran." + Environment.NewLine +
            "  - " + string.Join(Environment.NewLine + "  - ", failures));
    }

    [Fact]
    public void NoCellsDiscriminatorIsContainedInAnothers()
    {
        // The sibling test above asks whether a filter selects ANYTHING. This asks whether it
        // selects only what it names, which is a different question and became a live one the
        // moment a protocol was named as an extension of an existing one: RealHyde's method is
        // NdcgAt10_UnderCachedHydeOverRealChunking, so the parity Hyde cell's discriminator
        // "UnderCachedHyde" is a prefix of it and its printed filter now selects both cells.
        //
        // That is worse than selecting nothing, because it does not look like a failure. A reader
        // who follows the parity cell's command gets two measurements, the expensive one of which
        // they did not ask for and are not budgeted for -- and the run passes. The reranker pair
        // has the same shape.
        //
        // Substring rather than equality, and pairwise rather than against a list, because the
        // defect is containment: two discriminators can be distinct strings and still select
        // overlapping sets. Alternations are excluded deliberately -- `|` separates independent
        // discriminators and no cell emits one today, which the test below this one pins.
        var discriminators = new Dictionary<BeirProtocol, string>();
        foreach (var (_, protocol, filter) in BeirRunBudget.PrintedFilters())
        {
            if (filter.Contains('|', StringComparison.Ordinal))
            {
                continue;
            }

            discriminators[protocol] = filter.Split('&')[0].Replace(
                "DisplayName~", string.Empty, StringComparison.Ordinal);
        }

        var failures = new List<string>();
        foreach (var (protocol, discriminator) in discriminators)
        {
            foreach (var (otherProtocol, otherDiscriminator) in discriminators)
            {
                if (protocol == otherProtocol)
                {
                    continue;
                }

                if (otherDiscriminator.Contains(discriminator, StringComparison.Ordinal))
                {
                    failures.Add(
                        $"{protocol} prints \"{discriminator}\", which is contained in " +
                        $"{otherProtocol}'s \"{otherDiscriminator}\", so {protocol}'s command also " +
                        $"selects every {otherProtocol} case.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of the budget table's discriminators select more cells than they " +
            "name. The reader follows a skip message, gets a measurement they did not ask for " +
            "alongside the one they did, and the run reports success for both." +
            Environment.NewLine + "  - " +
            string.Join(Environment.NewLine + "  - ", failures));
    }

    [Fact]
    public void TheFilterParserReadsVstestsAlternationRatherThanSwallowingIt()
    {
        // Pinned here rather than left to the table, because no cell emits a `|` on this branch —
        // the first one that will is #229's GraphRag cell, which names the two classes now sharing
        // that cost. Without this the alternation support above is untested until that merges, and
        // "untested until somebody else's branch lands" is how the swallowed-pipe defect got here.
        //
        // Synthetic filters throughout, over this assembly's real test methods, so the cases are
        // stated rather than borrowed from whatever the table happens to hold today.
        const string Dataset = "scifact";
        var tests = TestMethods();

        // 1. Both alternatives name a real class: the filter selects tests, and the parser must not
        //    read "A|FullyQualifiedName~B" as one class name.
        Assert.Null(WhyNothingCanMatch(
            $"FullyQualifiedName~{nameof(BeirRunBudgetTests)}" +
            $"|FullyQualifiedName~{nameof(BeirParityTests)}",
            Dataset,
            tests));

        // 2. `|` binds looser than `&`. Read with the right precedence this is
        //    (DisplayName~GraphRag_OverTheMultiHopRagSlice & DisplayName~scifact) | (the class),
        //    whose left branch is dead for the ORIGINAL GraphRag reason — a [Fact] takes no
        //    datasetName, so no display name can carry a dataset — and whose right branch is fine.
        //    Split on `&` first and the branches interleave, and the dataset conjunct lands in the
        //    wrong alternative.
        var precedence = WhyNothingCanMatch(
            "DisplayName~GraphRag_OverTheMultiHopRagSlice&DisplayName~scifact" +
            $"|FullyQualifiedName~{nameof(GraphRagFunctionsTests)}",
            Dataset,
            tests);
        Assert.NotNull(precedence);
        Assert.Contains("DisplayName~scifact conjunct excludes every test", precedence, StringComparison.Ordinal);
        Assert.Contains("`datasetName` parameter", precedence, StringComparison.Ordinal);
        Assert.Contains(
            $"still selects tests through \"FullyQualifiedName~{nameof(GraphRagFunctionsTests)}\"",
            precedence,
            StringComparison.Ordinal);

        // 3. A filter with no `|` at all still produces the single, unwrapped reason it always did.
        //    The alternation vocabulary would be noise on a filter that has no alternatives, and
        //    every message the table can print today goes down this path.
        var single = WhyNothingCanMatch("DisplayName~NoSuchTestsClass", Dataset, tests);
        Assert.NotNull(single);
        Assert.StartsWith("no test method among the", single, StringComparison.Ordinal);
        Assert.DoesNotContain("alternative", single, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAlternativeThatSelectsNothingFailsEvenWhenItsSiblingMatches()
    {
        // The decision the alternation support above could have papered over, pinned so that it is
        // a decision rather than a side effect. "At least one alternative matches" is vstest's
        // SELECTION rule, and it is not this guard's PASS rule.
        //
        // The cell prices every case its filter names. A branch naming a class that has been
        // renamed away therefore means the pasted command measures a strict subset of what the
        // quoted cost bought — and exits 0 while doing it, because the surviving branch selected
        // something. That is the same silent-subset failure the empty selection was, minus the one
        // signal ("No test matches the given testcase filter") that made the empty one noticeable
        // at all. A sibling that saves the selection makes the rename quieter, not more correct, so
        // it fails; and it fails with different words from the everything-is-dead case, because a
        // reader who sees tests run needs telling why that is not the reassurance it looks like.
        const string Dataset = "scifact";
        var tests = TestMethods();

        var oneDeadBranch = WhyNothingCanMatch(
            $"FullyQualifiedName~{nameof(BeirRunBudgetTests)}|FullyQualifiedName~NoSuchTestsClass",
            Dataset,
            tests);
        Assert.NotNull(oneDeadBranch);
        Assert.Contains("1 of its 2 `|` alternatives", oneDeadBranch, StringComparison.Ordinal);
        Assert.Contains("NoSuchTestsClass", oneDeadBranch, StringComparison.Ordinal);
        Assert.Contains("strict subset", oneDeadBranch, StringComparison.Ordinal);

        // Every branch dead is the harder failure and has to say so differently: nothing is
        // selected at all, so there is no surviving side for a reader to be reassured by.
        var allDead = WhyNothingCanMatch(
            "FullyQualifiedName~NoSuchTestsClass|FullyQualifiedName~NorThisOne", Dataset, tests);
        Assert.NotNull(allDead);
        Assert.Contains("not one of its 2 `|` alternatives", allDead, StringComparison.Ordinal);
        Assert.Contains("selects nothing at all", allDead, StringComparison.Ordinal);
    }

    /// <summary>
    /// Says why one printed filter cannot select every test it names, or <see langword="null"/>
    /// when it can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>|</c> first, because it binds looser than <c>&amp;</c> in vstest's grammar: the filter is
    /// a list of alternatives, each of which is a conjunction. Splitting on <c>&amp;</c> first
    /// would put the operators the other way round and scatter one alternative's conjuncts across
    /// its neighbours.
    /// </para>
    /// <para>
    /// <b>Every alternative has to be able to match, not merely one of them.</b> One is what vstest
    /// needs to select something, and selecting something is not what a skip message promises — it
    /// promises the command that runs the case the quoted cost was measured for. A cell whose
    /// filter names two classes prices two runs; if one of those names has been renamed away, the
    /// pasted command runs the other and exits 0, and the cell is recorded as measured on half the
    /// work. That is the failure this guard exists for, with its one visible symptom — vstest's
    /// "No test matches the given testcase filter" — removed by the surviving branch.
    /// </para>
    /// </remarks>
    private static string? WhyNothingCanMatch(
        string filter, string dataset, IReadOnlyList<MethodInfo> tests)
    {
        var alternatives = filter.Split('|');
        var alone = alternatives.Length == 1;
        var dead = new List<string>(alternatives.Length);
        var alive = new List<string>(alternatives.Length);

        foreach (var alternative in alternatives)
        {
            // The subject travels down rather than being patched up on the way back, because the
            // sentence it lands in is a different claim in the two cases. "The filter selects
            // nothing at all" is true of a lone conjunction and false of one branch of an
            // alternation whose sibling matches — and printing it there would have this message
            // contradict its own opening clause in the reader's next breath.
            var failure = WhyThisAlternativeCannotMatch(
                alternative, dataset, tests, alone ? "the filter" : "this alternative");
            if (failure is null)
            {
                alive.Add(alternative);
                continue;
            }

            dead.Add(alone
                ? failure
                : $"The alternative \"{alternative}\" cannot match because {failure}");
        }

        if (dead.Count == 0)
        {
            return null;
        }

        if (alone)
        {
            return dead[0];
        }

        return alive.Count == 0
            ? NoAlternativeCanMatch(dead, alternatives.Length)
            : SomeAlternativesCannotMatch(dead, alive, alternatives.Length);
    }

    /// <summary>The message for an alternation in which nothing at all can be selected.</summary>
    private static string NoAlternativeCanMatch(IReadOnlyList<string> dead, int alternatives) =>
        $"not one of its {alternatives} `|` alternatives can match, so the filter selects nothing " +
        "at all — vstest reports \"No test matches the given testcase filter\" and EXITS 0. " +
        string.Join(" ", dead);

    /// <summary>
    /// The message for the alternation that still selects something, and is still wrong.
    /// </summary>
    private static string SomeAlternativesCannotMatch(
        IReadOnlyList<string> dead, IReadOnlyList<string> alive, int alternatives) =>
        $"{dead.Count} of its {alternatives} `|` alternatives cannot match. vstest ORs " +
        $"alternatives, so the filter still selects tests through \"{string.Join("\", \"", alive)}\" " +
        "and exits 0 looking correct — which makes this quieter than an empty selection rather " +
        "than better than one. The cell's cost covers every case its filter names, so a dead " +
        "branch means whoever pastes the command measures a strict subset of it and records the " +
        "whole cell as run. " + string.Join(" ", dead);

    /// <summary>
    /// Says why no test in this assembly can match one <c>&amp;</c>-conjoined alternative, or
    /// <see langword="null"/> when one can.
    /// </summary>
    /// <param name="filter">The alternative, with its <c>|</c> separators already removed.</param>
    /// <param name="dataset">The dataset whose conjunct is the second property checked.</param>
    /// <param name="tests">Every test method in this assembly.</param>
    /// <param name="subject">
    /// What selects nothing when the discriminators name nothing — the whole filter when it holds
    /// one alternative, and this alternative when it is one branch of several.
    /// </param>
    private static string? WhyThisAlternativeCannotMatch(
        string filter, string dataset, IReadOnlyList<MethodInfo> tests, string subject)
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
            return NothingIsNamed(discriminators, tests.Count, subject);
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
    private static string NothingIsNamed(
        IReadOnlyList<string> discriminators, int scanned, string subject) =>
        $"no test method among the {scanned} in this assembly carries " +
        $"\"{string.Join("\" and \"", discriminators)}\" in its name. The discriminator names a " +
        $"class or a method that has been renamed or removed, so {subject} selects nothing at all.";

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
