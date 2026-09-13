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

        foreach (var (dataset, protocol, selector) in BeirRunBudget.PrintedSelectors())
        {
            var failure = WhyNothingCanMatch(selector, tests);
            if (failure is not null)
            {
                failures.Add($"{dataset} / {protocol} prints {selector}, and {failure}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of the budget table's cells print a selector that cannot select " +
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
        foreach (var (_, protocol, selector) in BeirRunBudget.PrintedSelectors())
        {
            var patterns = Patterns(selector);

            // A cell emitting several selectors is an OR of independent discriminators, and
            // containment between independent branches is not the defect this looks for.
            // Only GraphRag does that today, which the test below this one pins.
            if (patterns.Count != 1)
            {
                continue;
            }

            discriminators[protocol] = patterns[0].Text;
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
    /// <summary>The parser reads each selector, rather than treating the fragment as one blob.</summary>
    /// <remarks>
    /// Multiple selectors act as an OR in the native runner, so a cell naming two classes prices
    /// two runs. A parser that saw one string would call that cell healthy while half of it had
    /// been renamed away — which is the VSTest-era failure this guard was built for, in new
    /// clothes.
    /// </remarks>
    [Fact]
    public void TheSelectorParserReadsEachSelectorRatherThanTheWholeFragment()
    {
        var tests = TestMethods();

        var both = Patterns(
            $"-class \"*{nameof(BeirRunBudgetTests)}\" -method \"*NdcgAt10*\"");
        Assert.Equal(2, both.Count);
        Assert.True(both[0].IsClass);
        Assert.Equal(nameof(BeirRunBudgetTests), both[0].Text, StringComparer.Ordinal);
        Assert.False(both[1].IsClass);
        Assert.Equal("NdcgAt10", both[1].Text, StringComparer.Ordinal);

        // A selector naming a real class selects something, so nothing is reported.
        Assert.Null(WhyNothingCanMatch($"-class \"*{nameof(BeirRunBudgetTests)}\"", tests));

        // A lone selector that names nothing reports the whole command as empty, without
        // reaching for the plural wording that only makes sense for an OR.
        var single = WhyNothingCanMatch($"-class \"*NoSuchTestsClass\"", tests);
        Assert.NotNull(single);
        Assert.Contains("selects nothing at all", single, StringComparison.Ordinal);
        Assert.DoesNotContain("selectors name", single, StringComparison.Ordinal);

        // A fragment with no selector in it at all is the worst case: the runner would be handed
        // no filter and would run the entire assembly.
        var none = WhyNothingCanMatch("--no-build", tests);
        Assert.NotNull(none);
        Assert.Contains("every test in the assembly", none, StringComparison.Ordinal);
    }

    /// <summary>One dead selector fails even when its sibling matches.</summary>
    /// <remarks>
    /// The quiet version of the defect: the command still runs, still exits 0, and measures a
    /// strict subset of what the cell priced. Distinguished from the all-dead case because the
    /// reader needs to know which it is.
    /// </remarks>
    [Fact]
    public void ASelectorThatNamesNothingFailsEvenWhenItsSiblingMatches()
    {
        var tests = TestMethods();

        var oneDead = WhyNothingCanMatch(
            $"-class \"*{nameof(BeirRunBudgetTests)}\" -class \"*NoSuchTestsClass\"", tests);
        Assert.NotNull(oneDead);
        Assert.Contains("1 of its 2 selectors", oneDead, StringComparison.Ordinal);
        Assert.Contains("SUBSET", oneDead, StringComparison.Ordinal);
        Assert.Contains("NoSuchTestsClass", oneDead, StringComparison.Ordinal);

        var allDead = WhyNothingCanMatch(
            $"-class \"*NoSuchTestsClass\" -method \"*NoSuchTestMethod*\"", tests);
        Assert.NotNull(allDead);
        Assert.Contains("not one of its 2 selectors", allDead, StringComparison.Ordinal);
        Assert.Contains("nothing at all", allDead, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every cell's printed command narrows to its own dataset, through the environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the half the native runner cannot do, and it is why the conversion lost
    /// nothing.</b> The old VSTest filter conjoined the dataset into itself. No filter the native
    /// runner accepts addresses a theory data row, so that conjunct could not survive the move —
    /// but it did not need to: <c>RAGNET_BEIR_LONG_RUNS</c> takes a comma-separated list of dataset
    /// names, and the same printed line already sets it.
    /// </para>
    /// <para>
    /// Without this, the conversion would have quietly widened every cell's command from one
    /// dataset to all of them, and the reader would pay for runs they never asked for under an exit
    /// code that says the cell ran as priced. That is the same failure the selector guard above
    /// exists for, arriving from the opposite direction.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryCellsCommandSelectsItsDatasetThroughTheEnvironment()
    {
        var failures = new List<string>();

        foreach (var (dataset, protocol, _) in BeirRunBudget.PrintedSelectors())
        {
            var printed = BeirRunBudget.ExplainFor(dataset, protocol);
            var expected = BeirRunBudget.OptInVariable + "=" + dataset;

            // The COMMAND line, not the whole message. The message also warns against the VSTest
            // filter by name, and a check over the prose would fail on its own caution — as this
            // one did when it was first written.
            var command = string.Empty;
            foreach (var line in printed.Split('\n'))
            {
                if (line.Contains(expected, StringComparison.Ordinal))
                {
                    command = line;
                    break;
                }
            }

            if (command.Length == 0)
            {
                failures.Add($"{dataset} / {protocol} prints no command line setting {expected}.");
                continue;
            }

            if (command.Contains("--filter", StringComparison.Ordinal))
            {
                failures.Add(
                    $"{dataset} / {protocol} still prints --filter, which raises RAGNET0001 under " +
                    "Microsoft.Testing.Platform and fails before a test runs.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "A cell's printed command must narrow to its own dataset, and must not use the VSTest " +
            "filter the build refuses:" + Environment.NewLine +
            "  - " + string.Join(Environment.NewLine + "  - ", failures));
    }

    /// <summary>One selector the printed command passes to the native runner.</summary>
    /// <param name="IsClass">Whether it is a <c>-class</c> rather than a <c>-method</c>.</param>
    /// <param name="Text">The name or fragment, with the runner's wildcards stripped.</param>
    private readonly record struct SelectorPattern(bool IsClass, string Text);

    /// <summary>Parses the selectors out of a printed command fragment.</summary>
    /// <param name="selector">The fragment <c>BeirRunBudget.Selector</c> produced.</param>
    /// <returns>Each selector, in order.</returns>
    /// <remarks>
    /// Parsed rather than taken as structured data on purpose: the string under test has to be the
    /// one the skip message prints. A guard fed the model behind it would be checking its own copy,
    /// and the copy is exactly what drifted last time.
    /// </remarks>
    private static List<SelectorPattern> Patterns(string selector)
    {
        var patterns = new List<SelectorPattern>();
        var index = 0;

        while (index < selector.Length)
        {
            var isClass = true;
            var at = selector.IndexOf("-class " + QUOTE, index, StringComparison.Ordinal);
            var methodAt = selector.IndexOf("-method " + QUOTE, index, StringComparison.Ordinal);

            if (at < 0 || (methodAt >= 0 && methodAt < at))
            {
                at = methodAt;
                isClass = false;
            }

            if (at < 0)
            {
                break;
            }

            var open = selector.IndexOf(QUOTE, at, StringComparison.Ordinal) + 1;
            var close = selector.IndexOf(QUOTE, open, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            patterns.Add(new SelectorPattern(isClass, selector[open..close].Trim('*')));
            index = close + 1;
        }

        return patterns;
    }

    /// <summary>The quote character the runner's arguments are wrapped in.</summary>
    private const string QUOTE = "\"";

    /// <summary>Why the printed selectors cannot select every test the cell prices, if so.</summary>
    /// <param name="selector">The printed command's selector fragment.</param>
    /// <param name="tests">Every test method in this assembly.</param>
    /// <returns>The reason, or <see langword="null"/> when every selector matches something.</returns>
    /// <remarks>
    /// <para>
    /// The third thing a skip message promises, after what did not run and what it costs: the
    /// command that runs it. That promise went unkept for a release once already, under the VSTest
    /// filter this replaced — a conjunction selected nothing, and <c>dotnet test</c> answered an
    /// empty selection with EXIT CODE 0.
    /// </para>
    /// <para>
    /// <b>The native runner does not make that failure impossible, only different.</b> Multiple
    /// selectors act as an OR, so a cell naming two classes prices two runs — and if one has been
    /// renamed away, the pasted command runs the other and exits 0, recording the cell as measured
    /// on half the work. Each selector is therefore checked on its own, not merely the union.
    /// </para>
    /// <para>
    /// <b>The dataset is no longer part of this.</b> The VSTest form conjoined it into the filter,
    /// which is why it could select nothing when a case took no dataset argument. The native runner
    /// cannot address a theory row at all, so the dataset now comes from
    /// <c>RAGNET_BEIR_LONG_RUNS</c>, which the same printed line sets and
    /// <see cref="EveryCellsCommandSelectsItsDatasetThroughTheEnvironment"/> pins.
    /// </para>
    /// </remarks>
    private static string? WhyNothingCanMatch(string selector, IReadOnlyList<MethodInfo> tests)
    {
        var patterns = Patterns(selector);
        if (patterns.Count == 0)
        {
            return "it names no -class or -method selector at all, so the runner would be handed "
                + "no filter and would run every test in the assembly — which is every other cell "
                + "in this table, under an exit code that says the cell ran as priced.";
        }

        var alone = patterns.Count == 1;
        var dead = new List<string>(patterns.Count);
        var alive = 0;

        foreach (var pattern in patterns)
        {
            var named = NamedBy(tests, in pattern);
            if (named.Count > 0)
            {
                alive++;
                continue;
            }

            var kind = pattern.IsClass ? "-class" : "-method";
            dead.Add(alone
                ? $"its {kind} {QUOTE}{pattern.Text}{QUOTE} names nothing among this assembly's "
                  + $"{tests.Count} test methods, so the command selects nothing at all."
                : $"The selector {kind} {QUOTE}{pattern.Text}{QUOTE} names nothing among this "
                  + $"assembly's {tests.Count} test methods.");
        }

        if (dead.Count == 0)
        {
            return null;
        }

        if (alone)
        {
            return dead[0];
        }

        return alive == 0
            ? $"not one of its {patterns.Count} selectors names a test, so the command selects "
              + "nothing at all and the runner exits 0 over an empty run. "
              + string.Join(" ", dead)
            : $"{dead.Count} of its {patterns.Count} selectors name nothing, so the command runs a "
              + "SUBSET of what the cell prices while exiting 0 as though it ran all of it. "
              + string.Join(" ", dead);
    }

    /// <summary>Every test the selector names.</summary>
    /// <param name="tests">Every test method in this assembly.</param>
    /// <param name="pattern">The selector.</param>
    /// <returns>The matches.</returns>
    /// <remarks>
    /// A <c>-class</c> matches on the declaring type's simple name and a <c>-method</c> on a
    /// fragment of the method name, which is how the runner's own wildcards behave. Reflection
    /// rather than a hardcoded list of names: a list would need editing by the same rename that
    /// breaks the selector, which moves the drift instead of catching it.
    /// </remarks>
    private static List<MethodInfo> NamedBy(
        IReadOnlyList<MethodInfo> tests, in SelectorPattern pattern)
    {
        var named = new List<MethodInfo>();
        foreach (var test in tests)
        {
            var matches = pattern.IsClass
                ? string.Equals(test.DeclaringType?.Name, pattern.Text, StringComparison.Ordinal)
                : test.Name.Contains(pattern.Text, StringComparison.Ordinal);

            if (matches)
            {
                named.Add(test);
            }
        }

        return named;
    }



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
