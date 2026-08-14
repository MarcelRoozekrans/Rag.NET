using System.Globalization;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// What the GraphRAG generation tool was asked to do: which stage, over which corpus, and how much
/// of it.
/// <para>
/// <b>Parsed here rather than in the tool, so it can be tested where money is not at stake.</b> The
/// tool is a console application whose entry point no test project in the fast tier can see — the
/// same reason <see cref="MultiHopRagSliceWalk"/> lives beside this file. An argument parser that
/// silently defaulted an unrecognised corpus name to the slice, or accepted
/// <c>--corpus ful</c> as "full", would spend a six-hour budget on the wrong articles and say
/// nothing, so every rejection below is asserted by a unit test.
/// </para>
/// </summary>
/// <param name="Corpus">Which articles to extract. Defaults to
/// <see cref="GraphExtractionCorpus.Slice"/>.</param>
/// <param name="MaxDocuments">
/// An upper bound on how many articles are taken, for a smoke run.
/// <see cref="int.MaxValue"/> means "all of them", which is what absence of the flag means.
/// </param>
/// <param name="Stage">
/// Which GraphRAG stage to generate. Defaults to <see cref="GraphRagGenerationStage.Extraction"/>,
/// which is what the tool did before there was a second stage — an optional parameter rather than a
/// new positional one so that no existing construction of this record changes meaning.
/// </param>
/// <param name="PlanOnly">
/// Whether to print the plan and stop, generating nothing.
/// <para>
/// <b>It exists so that "what would this cost" can be asked without answering "spend it".</b> Both
/// stages cost themselves against the cache before they start, but a plan followed immediately by
/// the run it planned is a plan nobody can act on: learning that a full-corpus report run would
/// generate several hundred summaries is only useful if learning it is free. In this mode no API
/// key is read and no model is constructed, so a run that would spend cannot begin by accident.
/// </para>
/// </param>
public sealed record GraphExtractionRunOptions(
    GraphExtractionCorpus Corpus,
    int MaxDocuments,
    GraphRagGenerationStage Stage = GraphRagGenerationStage.Extraction,
    bool PlanOnly = false)
{
    /// <summary>The flag naming the corpus.</summary>
    public const string CorpusOption = "--corpus";

    /// <summary>The flag bounding how many articles are taken.</summary>
    public const string MaxDocumentsOption = "--max-documents";

    /// <summary>The flag naming the stage.</summary>
    public const string StageOption = "--stage";

    /// <summary>The valueless flag that prints the plan and stops.</summary>
    public const string PlanOnlyOption = "--plan-only";

    /// <summary>The value of <see cref="CorpusOption"/> selecting the pinned slice.</summary>
    public const string SliceName = "slice";

    /// <summary>The value of <see cref="CorpusOption"/> selecting the whole corpus.</summary>
    public const string FullName = "full";

    /// <summary>The value of <see cref="StageOption"/> selecting entity extraction.</summary>
    public const string ExtractionStageName = "extraction";

    /// <summary>The value of <see cref="StageOption"/> selecting community reports.</summary>
    public const string ReportsStageName = "reports";

    /// <summary>The usage line printed when parsing fails.</summary>
    public const string Usage =
        "Usage: Rag.NET.Benchmarks.Quality.GraphExtractions [--stage extraction|reports] [--corpus slice|full] [--max-documents N]\n" +
        "  --stage extraction  entities and relationships, one call per chunk plus gleaning (default)\n" +
        "  --stage reports     community reports over the graph those extractions build, one call per community;\n" +
        "                      it replays extraction refuse-on-miss and never extracts anything itself\n" +
        "  --corpus slice   the pinned sixty-article MultiHop-RAG slice the GraphRAG guard reads (default)\n" +
        "  --corpus full    every article in the converted corpus — hours of wall clock and real money\n" +
        "  --max-documents  take at most N articles of whichever corpus, for a smoke run\n" +
        "  --plan-only      print what the run would cost and stop; reads no API key and calls no model";

    /// <summary>
    /// Gets what an invocation with no arguments means: extraction over the slice, all of it —
    /// exactly what the tool did before there was anything else to ask for.
    /// </summary>
    public static GraphExtractionRunOptions Default { get; } =
        new(GraphExtractionCorpus.Slice, int.MaxValue);

    /// <summary>Parses the command line.</summary>
    /// <param name="args">The arguments, as <c>Main</c> received them.</param>
    /// <returns>
    /// The options, or <see langword="null"/> when the command line is not understood — an unknown
    /// flag, an unknown corpus name, a flag without a value, a repeated flag, or a non-positive
    /// document bound. The caller prints <see cref="Usage"/> and exits rather than guessing.
    /// </returns>
    public static GraphExtractionRunOptions? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = Default;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            // A repeated flag is refused rather than last-one-wins: "--corpus full --corpus slice"
            // is a typo whose two readings differ by six hours and a bill.
            if (!seen.Add(args[i]))
            {
                return null;
            }

            // The one flag that takes no value, so the cursor advances by one rather than two.
            // Everything else is a name and a value, and a name at the end of the line with no
            // value is refused rather than defaulted.
            if (string.Equals(args[i], PlanOnlyOption, StringComparison.Ordinal))
            {
                options = options with { PlanOnly = true };
                continue;
            }

            if (i + 1 >= args.Count)
            {
                return null;
            }

            var applied = Apply(options, args[i], args[i + 1]);
            if (applied is null)
            {
                return null;
            }

            options = applied;
            i++;
        }

        return options;
    }

    /// <summary>Applies one flag and its value, or <see langword="null"/> if either is unknown.</summary>
    private static GraphExtractionRunOptions? Apply(
        GraphExtractionRunOptions options, string name, string value) =>
        name switch
        {
            CorpusOption => TryParseCorpus(value, out var corpus)
                ? options with { Corpus = corpus }
                : null,
            MaxDocumentsOption => TryParseMaxDocuments(value, out var max)
                ? options with { MaxDocuments = max }
                : null,
            StageOption => TryParseStage(value, out var stage)
                ? options with { Stage = stage }
                : null,
            _ => null,
        };

    /// <summary>
    /// Maps a stage name to its mode. Only the two names below, in any casing; anything else is
    /// refused.
    /// </summary>
    /// <remarks>
    /// Written out rather than delegating to <c>Enum.TryParse</c>, for the reason
    /// <see cref="TryParseCorpus"/> is — and with more at stake, since the two stages send
    /// different prompts to the same model and a numeric literal reading as "reports" would spend
    /// the budget on the stage nobody named.
    /// </remarks>
    private static bool TryParseStage(string value, out GraphRagGenerationStage stage)
    {
        if (string.Equals(value, ExtractionStageName, StringComparison.OrdinalIgnoreCase))
        {
            stage = GraphRagGenerationStage.Extraction;
            return true;
        }

        if (string.Equals(value, ReportsStageName, StringComparison.OrdinalIgnoreCase))
        {
            stage = GraphRagGenerationStage.Reports;
            return true;
        }

        stage = GraphRagGenerationStage.Extraction;
        return false;
    }

    /// <summary>
    /// Maps a corpus name to its mode. Only the two names below, in any casing; anything else is
    /// refused.
    /// </summary>
    /// <remarks>
    /// Written out rather than delegating to <c>Enum.TryParse</c>, which would also accept
    /// <c>"0"</c> and <c>"1"</c> — a numeric literal reading as a corpus is exactly the silent
    /// mis-selection this method exists to prevent.
    /// </remarks>
    private static bool TryParseCorpus(string value, out GraphExtractionCorpus corpus)
    {
        if (string.Equals(value, SliceName, StringComparison.OrdinalIgnoreCase))
        {
            corpus = GraphExtractionCorpus.Slice;
            return true;
        }

        if (string.Equals(value, FullName, StringComparison.OrdinalIgnoreCase))
        {
            corpus = GraphExtractionCorpus.Full;
            return true;
        }

        corpus = GraphExtractionCorpus.Slice;
        return false;
    }

    /// <summary>Parses a positive document bound; no sign, no separators, invariant.</summary>
    private static bool TryParseMaxDocuments(string value, out int maxDocuments) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out maxDocuments)
        && maxDocuments > 0;
}
