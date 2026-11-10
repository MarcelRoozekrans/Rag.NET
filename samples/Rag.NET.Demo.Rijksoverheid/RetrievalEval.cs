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

    // FirstRank is the 0-based rank of the first answer page, -1 when none is in the results;
    // Top1 is the page ranked first, or null when retrieval returned nothing.
    private sealed record Score(double Hit1, double Hit5, double Recall5, double Mrr10, int FirstRank, string? Top1);

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
        IReadOnlySet<string> ingestedUrls,
        string? poolPath = null)
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
        // Per question: every page some variant put in its top 5, with the text of its best chunk.
        var pool = answerable.Select(_ => new Dictionary<string, PoolEntry>(StringComparer.Ordinal)).ToList();
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
                    scores.Add(new Score(0, 0, 0, 0, -1, null));
                    continue;
                }

                var ranked = RankedPages(result.Value);
                scores.Add(ScoreOne(ranked, expected[i]));
                AddToPool(pool[i], result.Value, variant.Name);
            }

            perVariant[variant.Name] = scores;
            Console.WriteLine($"  {variant.Name}: done");
        }

        PrintTable(perVariant, variants[0].Name);
        PrintMisses(answerable, perVariant[variants[0].Name], variants[0].Name);
        PrintMovers(answerable, perVariant, variants[0].Name);

        if (poolPath is not null)
        {
            await WritePoolAsync(poolPath, answerable, expected, pool);
        }
    }

    private sealed record PoolEntry(string Text, List<string> Variants);

    private sealed record PoolPage(string Url, bool Labelled, IReadOnlyList<string> Variants, string Text);

    private sealed record PoolQuestion(string Vraag, IReadOnlyList<PoolPage> Pages);

    private static void AddToPool(
        Dictionary<string, PoolEntry> pool,
        IReadOnlyList<Rag.NET.Models.SearchResult> results,
        string variant)
    {
        // Results are in rank order, so the first chunk seen of a page is its best one.
        var pages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            var url = r.Chunk.DocumentId.Value;
            if (!pages.Add(url)) continue;
            if (pages.Count > 5) break;

            if (!pool.TryGetValue(url, out var entry))
            {
                entry = new PoolEntry(r.Chunk.Text, []);
                pool[url] = entry;
            }

            entry.Variants.Add(variant);
        }
    }

    /// <summary>
    /// Writes the top-5 pool for judging which unlabelled pages also answer their question. Judging
    /// the whole pool rather than one variant's results keeps the added labels fair to every variant.
    /// </summary>
    private static async Task WritePoolAsync(
        string path,
        List<Question> questions,
        List<List<string>> expected,
        List<Dictionary<string, PoolEntry>> pool)
    {
        var output = questions
            .Select((q, i) => new PoolQuestion(
                q.Vraag,
                pool[i]
                    .Select(kv => new PoolPage(kv.Key, expected[i].Contains(kv.Key, StringComparer.Ordinal), kv.Value.Variants, kv.Value.Text))
                    .OrderBy(page => page.Labelled)
                    .ToList()))
            .ToList();

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, output, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        var unlabelled = output.Sum(q => q.Pages.Count(page => !page.Labelled));
        Console.WriteLine($"\nTop-5 pool written to {Path.GetFullPath(path)}: {unlabelled} unlabelled pages to judge.");
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
            Mrr10: firstRank is >= 0 and < TopK ? 1.0 / (firstRank + 1) : 0,
            FirstRank: firstRank,
            Top1: ranked.Count > 0 ? ranked[0] : null);
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
            var row = Summarise(scores, baseMrr);
            Console.WriteLine(string.Format(inv,
                "| {0} | {1:0.000} | {2:0.000} | {3:0.000} | {4:0.000} | {5} |",
                name,
                row.Hit1,
                row.Hit5,
                row.Recall5,
                row.Mrr10,
                string.Equals(name, baseline, StringComparison.Ordinal) ? "—" : $"{row.Better} / {row.Worse}"));
        }
    }

    /// <summary>One variant's table row: mean scores, and how many questions beat or trail the baseline's MRR.</summary>
    private static (double Hit1, double Hit5, double Recall5, double Mrr10, int Better, int Worse) Summarise(
        List<Score> scores, List<double> baseMrr)
    {
        double hit1 = 0, hit5 = 0, recall5 = 0, mrr10 = 0;
        int better = 0, worse = 0;
        for (var i = 0; i < scores.Count; i++)
        {
            var s = scores[i];
            hit1 += s.Hit1;
            hit5 += s.Hit5;
            recall5 += s.Recall5;
            mrr10 += s.Mrr10;
            if (s.Mrr10 > baseMrr[i]) better++;
            else if (s.Mrr10 < baseMrr[i]) worse++;
        }

        var n = scores.Count;
        return (hit1 / n, hit5 / n, recall5 / n, mrr10 / n, better, worse);
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

    /// <summary>
    /// Per variant, the questions it ranks differently from the baseline: where the first answer
    /// page sits in each, and what the variant put first when that was not an answer page. The
    /// better/worse counts say how many moved; this says which, so a loss can be read.
    /// </summary>
    private static void PrintMovers(List<Question> questions, Dictionary<string, List<Score>> perVariant, string baseline)
    {
        var baseScores = perVariant[baseline];
        foreach (var (name, scores) in perVariant)
        {
            if (string.Equals(name, baseline, StringComparison.Ordinal)) continue;

            var moved = Moved(scores, baseScores);
            if (moved.Count == 0) continue;

            Console.WriteLine($"\n{name} vs {baseline} (rank of the first answer page, 1-based; - = not in the top {TopK}):");
            foreach (var i in moved)
            {
                var arrow = scores[i].Mrr10 > baseScores[i].Mrr10 ? "better" : "worse ";
                Console.WriteLine($"  {arrow} {Rank(baseScores[i])} -> {Rank(scores[i])}  {questions[i].Vraag}");
                if (scores[i].FirstRank != 0 && scores[i].Top1 is { } top1)
                {
                    Console.WriteLine($"           {name} ranked first: {top1}");
                }
            }
        }
    }

    /// <summary>
    /// The questions whose MRR differs from the baseline, the ones the variant improved first,
    /// each group in question order.
    /// </summary>
    private static List<int> Moved(List<Score> scores, List<Score> baseScores)
    {
        var better = new List<int>();
        var worse = new List<int>();
        for (var i = 0; i < scores.Count; i++)
        {
            if (scores[i].Mrr10 > baseScores[i].Mrr10) better.Add(i);
            else if (scores[i].Mrr10 < baseScores[i].Mrr10) worse.Add(i);
        }

        better.AddRange(worse);
        return better;
    }
    private static string Rank(Score s) =>
        s.FirstRank is >= 0 and < TopK ? (s.FirstRank + 1).ToString(CultureInfo.InvariantCulture) : "-";
}
