using System.Globalization;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// What the extraction generation tool was asked to do: which corpus, and how much of it.
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
public sealed record GraphExtractionRunOptions(GraphExtractionCorpus Corpus, int MaxDocuments)
{
    /// <summary>The flag naming the corpus.</summary>
    public const string CorpusOption = "--corpus";

    /// <summary>The flag bounding how many articles are taken.</summary>
    public const string MaxDocumentsOption = "--max-documents";

    /// <summary>The value of <see cref="CorpusOption"/> selecting the pinned slice.</summary>
    public const string SliceName = "slice";

    /// <summary>The value of <see cref="CorpusOption"/> selecting the whole corpus.</summary>
    public const string FullName = "full";

    /// <summary>The usage line printed when parsing fails.</summary>
    public const string Usage =
        "Usage: Rag.NET.Benchmarks.Quality.GraphExtractions [--corpus slice|full] [--max-documents N]\n" +
        "  --corpus slice   the pinned sixty-article MultiHop-RAG slice the GraphRAG guard reads (default)\n" +
        "  --corpus full    every article in the converted corpus — hours of wall clock and real money\n" +
        "  --max-documents  take at most N articles of whichever corpus, for a smoke run";

    /// <summary>
    /// Gets what an invocation with no arguments means: the slice, all of it — exactly what the
    /// tool did before there was anything else to ask for.
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

        for (var i = 0; i < args.Count; i += 2)
        {
            // A repeated flag is refused rather than last-one-wins: "--corpus full --corpus slice"
            // is a typo whose two readings differ by six hours and a bill.
            if (i + 1 >= args.Count || !seen.Add(args[i]))
            {
                return null;
            }

            var applied = Apply(options, args[i], args[i + 1]);
            if (applied is null)
            {
                return null;
            }

            options = applied;
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
            _ => null,
        };

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
