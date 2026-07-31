namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Which measurements the nightly can afford to run, and what the rest cost — the one place the
/// split between them is decided.
/// <para>
/// <b>Why there is a budget at all.</b> <c>nightly.yml</c>'s <c>env-gated</c> job has
/// <c>timeout-minutes: 120</c> and spends part of that restoring, building the whole solution and
/// running four other <c>&lt;RequiresSecrets&gt;</c> projects before this one starts. Phase 3.12
/// Task 4 added FiQA, whose parity run measures 1 h 11 m and whose real run has never been run to
/// completion at an estimated 8–9 h. There is no arrangement of a 120-minute job in which those two
/// finish, so the job as it stood would have timed out — and <b>a timeout reports nothing about
/// parity</b>, which is the same silence this workflow was fixed to stop producing.
/// </para>
/// <para>
/// <b>Why the costs are the cold ones.</b> <c>RAGNET_BEIR_CACHE</c> points at
/// <c>RUNNER_TEMP/beir</c>, which is a fresh directory every night. Nothing is cached across runs,
/// so <see cref="EmbeddingCache"/> saves the nightly nothing and every figure recorded here is the
/// price paid from empty. The warm figures are quoted alongside only because they are what a
/// developer re-running a case locally will actually see, and a cost table that quoted them as the
/// cost would understate the nightly by an order of magnitude.
/// </para>
/// <para>
/// <b>What the nightly keeps, and why that is the right half.</b> SciFact and ArguAna under
/// <see cref="BeirProtocol.Parity"/> — roughly 15–20 minutes cold, all four cases — because parity
/// is the only protocol whose number can be checked against a published one, and that number is
/// what this milestone exists to protect. Everything else is opt-in. That is not a judgement that
/// the real runs matter less; it is that 28 measured minutes for ArguAna's real leg plus SciFact's
/// (never timed on its own; see its entry) on a hosted runner slower than the machine these were
/// measured on is how the timeout comes back.
/// </para>
/// <para>
/// <b>What that costs, stated rather than buried.</b> No chunk-to-document max-pooling runs against
/// a corpus in the nightly any more. What still runs there is
/// <see cref="BeirRealChunkingTests.Chunking_SplitsEveryCorpusIntoMoreUnitsThanDocuments"/>, which
/// needs no model, finishes in seconds and catches a chunker that stopped chunking — half of the
/// guard, at none of the cost. The pooling half is
/// <c>DocumentRankingTests</c>' fixture and an opt-in run.
/// </para>
/// </summary>
public static class BeirRunBudget
{
    /// <summary>
    /// The variable that opts a run into the cases the nightly cannot afford.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> set by <c>nightly.yml</c>, and listed in that job's presence report
    /// anyway so the log says plainly that the long runs were off rather than leaving a reader to
    /// infer it from a test count.
    /// </remarks>
    public const string OptInVariable = "RAGNET_BEIR_LONG_RUNS";

    /// <summary>
    /// Every dataset under every protocol, with what it cost when it was last timed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table rather than a rule. "FiQA is expensive" would be a rule, and it would quietly decide
    /// the answer for the fourth dataset somebody adds; a table cannot, because
    /// <see cref="Find"/> throws on a pair that is not in it. Adding a descriptor to
    /// <see cref="BeirDatasetDescriptor.All"/> therefore fails here, loudly, until someone has
    /// measured what they added.
    /// </para>
    /// <para>
    /// Every string is a measurement or says that it is not one. The alternative — round numbers
    /// nobody timed — is what produced a 120-minute job containing a nine-hour test.
    /// </para>
    /// </remarks>
    private static readonly Cost[] Costs =
    [
        new(
            "scifact",
            BeirProtocol.Parity,
            FitsTheNightly: true,
            "~5 min per separator, cold. The theory runs both separators, so ~10 min for the dataset."),
        new(
            "scifact",
            BeirProtocol.Real,
            FitsTheNightly: false,
            "~19 min cold, DERIVED AND NOT DIRECTLY TIMED: ArguAna's real leg measured 28 min over " +
            "82,618 chunks, and SciFact's 56,707 chunks are the same shape of text. Time it before " +
            "quoting it as measured."),
        new(
            "fiqa",
            BeirProtocol.Parity,
            FitsTheNightly: false,
            "1 h 11 m, measured. One separator only — FiQA titles none of its 57,638 documents, so " +
            "the second separator would re-derive a number that is equal by construction."),
        new(
            "fiqa",
            BeirProtocol.Real,
            FitsTheNightly: false,
            "~8-9 h, ESTIMATED: it has never been run to completion. The real leg embeds 429,850 " +
            "chunks — 7.5x the corpus — and InMemoryVectorStore sorts all of them per query for " +
            "6,648 queries."),
        new(
            "arguana",
            BeirProtocol.Parity,
            FitsTheNightly: true,
            "~4 min per separator cold, 50 s with the embedding cache warm. The theory runs both " +
            "separators, so ~8 min cold for the dataset."),
        new(
            "arguana",
            BeirProtocol.Real,
            FitsTheNightly: false,
            "28 min, measured, both legs. The only case that has been timed exercising max-pooling " +
            "against a whole corpus."),
    ];

    /// <summary>
    /// Reports whether this case is one the nightly cannot afford and nobody asked for.
    /// </summary>
    /// <param name="datasetName">The BEIR dataset name, as it appears in the theory data.</param>
    /// <param name="protocol">Which protocol the case measures under.</param>
    /// <param name="reason">
    /// Receives the skip message when the result is <see langword="true"/>, and
    /// <see cref="string.Empty"/> otherwise.
    /// </param>
    /// <returns><see langword="true"/> when the case must be skipped.</returns>
    /// <remarks>
    /// Shaped like <see cref="BeirHarness.IsProvisioned"/> on purpose, so both gates read the same
    /// way at the top of a test and <c>Assert.SkipWhen</c> stays visible in the test itself rather
    /// than being buried in a helper that skips on the caller's behalf.
    /// </remarks>
    public static bool IsGatedOff(string datasetName, BeirProtocol protocol, out string reason)
    {
        var cost = Find(datasetName, protocol);
        if (cost.FitsTheNightly || IsOptedIn())
        {
            reason = string.Empty;
            return false;
        }

        reason = Explain(cost);
        return true;
    }

    /// <summary>Reports whether the long runs were explicitly asked for.</summary>
    /// <returns><see langword="true"/> when <see cref="OptInVariable"/> asks for them.</returns>
    /// <remarks>
    /// Presence is not enough on its own: <c>RAGNET_BEIR_LONG_RUNS=0</c> in a workflow reads to
    /// every human as "off", and a gate that turned nine hours of measurement on for it would be a
    /// trap rather than a switch. "0" and "false" are therefore off, and anything else present is
    /// on.
    /// </remarks>
    private static bool IsOptedIn()
    {
        var value = Environment.GetEnvironmentVariable(OptInVariable);

        return !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Finds the recorded cost for one dataset under one protocol.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been measured for that pair.</exception>
    private static Cost Find(string datasetName, BeirProtocol protocol)
    {
        foreach (var cost in Costs)
        {
            if (string.Equals(cost.Dataset, datasetName, StringComparison.Ordinal)
                && cost.Protocol == protocol)
            {
                return cost;
            }
        }

        throw new InvalidOperationException(
            $"No run cost is recorded for dataset '{datasetName}' under the {protocol} protocol. " +
            $"A dataset was added to BeirDatasetDescriptor.All without measuring what it costs, and " +
            $"{nameof(BeirRunBudget)} refuses to guess: an unmeasured case either silently joins a " +
            "120-minute nightly job or is silently gated out of it, and both have happened. Time it " +
            $"with {OptInVariable}=1 and add it to {nameof(BeirRunBudget)}.{nameof(Costs)}.");
    }

    /// <summary>
    /// The message a gated case skips with: what did not run, what it costs, and the command that
    /// runs it.
    /// </summary>
    /// <remarks>
    /// All three parts are required and none is decoration. A skip that says only "skipped" is
    /// indistinguishable from a pass in a test summary — the failure mode this project has spent
    /// three phases removing — and one that names the case without naming the cost invites the next
    /// person to put it back in the nightly, which is how tonight's timeout was built.
    /// </remarks>
    private static string Explain(Cost cost) =>
        $"""
        {cost.Dataset} {Describe(cost.Protocol)} run is OPT-IN and did NOT run.
        Measured cost: {cost.Measured}
        Why: nightly.yml's env-gated job has timeout-minutes: 120, and also restores, builds the whole
        solution and runs four other <RequiresSecrets> projects. RAGNET_BEIR_CACHE is RUNNER_TEMP/beir,
        fresh every night, so the embedding cache saves that job nothing and the cost above is what it
        would pay. The nightly keeps SciFact and ArguAna PARITY (~15-20 min cold, all four cases),
        which is the published number this milestone exists to protect.
        To run this case:
          {OptInVariable}=1 dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build --filter "{Filter(cost)}"
        """;

    /// <summary>Names the protocol the way the run's own output does.</summary>
    private static string Describe(BeirProtocol protocol) => protocol == BeirProtocol.Parity
        ? "PARITY (one chunk per document, truncated at 256)"
        : "REAL (RecursiveChunkingStrategy at defaults, max-pooled to documents)";

    /// <summary>
    /// The <c>--filter</c> that selects exactly this case.
    /// </summary>
    /// <remarks>
    /// <c>DisplayName</c> on both halves, never <c>FullyQualifiedName</c>: the latter stops at the
    /// method name and carries no theory arguments, so it matches every dataset or none. Both test
    /// files already document this, having checked it rather than assumed it.
    /// </remarks>
    private static string Filter(Cost cost)
    {
        var testClass = cost.Protocol == BeirProtocol.Parity
            ? nameof(BeirParityTests)
            : nameof(BeirRealChunkingTests);

        return $"DisplayName~{testClass}&DisplayName~{cost.Dataset}";
    }

    /// <summary>What one dataset costs under one protocol, and whether the nightly can afford it.</summary>
    /// <param name="Dataset">The BEIR dataset name.</param>
    /// <param name="Protocol">The protocol measured.</param>
    /// <param name="FitsTheNightly">Whether the case runs without being asked for.</param>
    /// <param name="Measured">
    /// What it cost when it was last timed, in prose, and saying so when the figure is an estimate
    /// rather than a measurement.
    /// </param>
    private sealed record Cost(
        string Dataset, BeirProtocol Protocol, bool FitsTheNightly, string Measured);
}
