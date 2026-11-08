using System.Globalization;
using System.Text.Json;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

/// <summary>
/// Measures retrieval variants against a hand-written question set with known answer pages.
/// </summary>
/// <remarks>
/// Scored per page, not per chunk: several chunks of one page count once, at the rank of the
/// first. A question can name more than one page that answers it; Hit@k means any of them is in
/// the top k, Recall@5 the share of them that is.
/// </remarks>
internal static class RetrievalEval
{
    private const int TopK = 10;

    internal sealed record Question(string Vraag, IReadOnlyList<string> Urls);

    internal sealed record Variant(string Name, RetrievalOptions Options);

    private sealed record Score(double Hit1, double Hit5, double Recall5, double Mrr10);

    public static async Task<IReadOnlyList<Question>> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var questions = await JsonSerializer.DeserializeAsync<List<Question>>(
            stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return questions ?? [];
    }

    /// <summary>
    /// The variants to compare. Each switches off what it does not test, so a registered
    /// component cannot leak into another variant through a <see cref="RetrievalOptions"/>
    /// default (<c>UseHyde</c> and <c>UseReranking</c> default to true).
    /// </summary>
    public static IReadOnlyList<Variant> Variants(bool rerankerRegistered)
    {
        var dense = new RetrievalOptions
        {
            TopK = TopK,
            UseHybridSearch = false,
            UseHyde = false,
            UseMultiQuery = false,
            UseReranking = false,
            UseCacheEmbedding = false,
            UseCacheResult = false,
        };

        var variants = new List<Variant>
        {
            new("dense", dense),
            new("hybrid", dense with { UseHybridSearch = true }),
            new("hyde", dense with { UseHyde = true }),
            new("hybrid+hyde", dense with { UseHybridSearch = true, UseHyde = true }),
            // The site publishes one FAQ answer under up to four theme paths: four documents with
            // the same text, which can fill the top 5 with one answer.
            new("mmr", dense with { UseMmr = true, MmrLambda = 0.7f, MmrCandidateCount = 30 }),
        };

        if (rerankerRegistered)
        {
            variants.Add(new("rerank", dense with { UseReranking = true, CandidateCount = 30 }));
            variants.Add(new("hybrid+rerank", dense with { UseHybridSearch = true, UseReranking = true, CandidateCount = 30 }));
        }

        return variants;
    }

    public static async Task RunAsync(
        IRagPipeline pipeline,
        IReadOnlyList<Question> questions,
        IReadOnlyList<Variant> variants,
        IReadOnlySet<string> ingestedUrls)
    {
        // A question whose answer page was never ingested measures the crawl, not retrieval.
        var answerable = questions
            .Where(q => q.Urls.Any(ingestedUrls.Contains))
            .ToList();
        var dropped = questions.Count - answerable.Count;
        Console.WriteLine($"\n{answerable.Count} questions; {dropped} dropped because no answer page was ingested.\n");

        // The answer pages that were actually ingested, once per question rather than per variant.
        var expected = answerable
            .Select(q => q.Urls.Where(ingestedUrls.Contains).ToList())
            .ToList();

        var perVariant = new Dictionary<string, List<Score>>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            var scores = new List<Score>(answerable.Count);
            for (var i = 0; i < answerable.Count; i++)
            {
                var question = answerable[i];
                var result = await pipeline.RetrieveAsync(question.Vraag, variant.Options);
                if (!result.IsSuccess)
                {
                    Console.WriteLine($"  [{variant.Name}] retrieval failed for \"{question.Vraag}\": {result.Error}");
                    scores.Add(new Score(0, 0, 0, 0));
                    continue;
                }

                scores.Add(ScoreOne(RankedPages(result.Value), expected[i]));
            }

            perVariant[variant.Name] = scores;
            Console.WriteLine($"  {variant.Name}: done");
        }

        PrintTable(perVariant, variants[0].Name);
        PrintMisses(answerable, perVariant[variants[0].Name], variants[0].Name);
    }

    /// <summary>Page URLs in rank order, each at the rank of its first chunk.</summary>
    private static List<string> RankedPages(IReadOnlyList<Rag.NET.Models.SearchResult> results)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ranked = new List<string>(results.Count);
        foreach (var r in results)
        {
            if (seen.Add(r.Chunk.DocumentId.Value)) ranked.Add(r.Chunk.DocumentId.Value);
        }

        return ranked;
    }

    private static Score ScoreOne(List<string> ranked, List<string> expected)
    {
        var firstRank = ranked.FindIndex(url => expected.Contains(url, StringComparer.Ordinal));
        var inTop5 = ranked.Take(5).Count(url => expected.Contains(url, StringComparer.Ordinal));
        return new Score(
            Hit1: firstRank == 0 ? 1 : 0,
            Hit5: firstRank is >= 0 and < 5 ? 1 : 0,
            Recall5: (double)inTop5 / expected.Count,
            Mrr10: firstRank is >= 0 and < TopK ? 1.0 / (firstRank + 1) : 0);
    }

    private static void PrintTable(Dictionary<string, List<Score>> perVariant, string baseline)
    {
        var inv = CultureInfo.InvariantCulture;
        var baseMrr = perVariant[baseline].Select(s => s.Mrr10).ToList();

        Console.WriteLine();
        Console.WriteLine("| variant | Hit@1 | Hit@5 | Recall@5 | MRR@10 | MRR better / worse vs " + baseline + " |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|");
        foreach (var (name, scores) in perVariant)
        {
            var better = scores.Where((s, i) => s.Mrr10 > baseMrr[i]).Count();
            var worse = scores.Where((s, i) => s.Mrr10 < baseMrr[i]).Count();
            Console.WriteLine(string.Format(inv,
                "| {0} | {1:0.000} | {2:0.000} | {3:0.000} | {4:0.000} | {5} |",
                name,
                scores.Average(s => s.Hit1),
                scores.Average(s => s.Hit5),
                scores.Average(s => s.Recall5),
                scores.Average(s => s.Mrr10),
                string.Equals(name, baseline, StringComparison.Ordinal) ? "—" : $"{better} / {worse}"));
        }
    }

    private static void PrintMisses(List<Question> questions, List<Score> scores, string name)
    {
        var misses = questions.Where((_, i) => scores[i].Hit5 == 0).ToList();
        if (misses.Count == 0) return;

        Console.WriteLine($"\nNot in {name}'s top 5:");
        foreach (var q in misses)
        {
            Console.WriteLine($"  - {q.Vraag}  →  {string.Join(", ", q.Urls)}");
        }
    }
}
