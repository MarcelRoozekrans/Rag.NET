using Microsoft.Extensions.AI;
using OpenAI;
using Rag.NET.Models.Options;
using System.ClientModel;
using System.Globalization;

namespace Rag.NET.Benchmarks.Quality.Hypotheticals;

/// <summary>
/// The one-time HyDE hypothetical generation tool. Reads every <b>evaluated</b> query — the ones
/// with a judgement in <c>qrels/test.tsv</c> — from each BEIR dataset, generates
/// <see cref="HydeOptions.HypothesisCount"/> hypothetical answer passages per query through
/// OpenRouter, and writes them into <see cref="HypotheticalCache"/>.
/// <para>
/// <b>The cached text is the experiment.</b> Hosted LLMs are not bit-deterministic even at
/// temperature 0, so the ablation table's HyDE row never calls an LLM: it reads what this tool
/// wrote, verbatim, forever. That is also why this tool is resumable — every entry already on disk
/// is skipped, so an interrupted run continues instead of regenerating text it can never reproduce.
/// </para>
/// <para>
/// Usage: <c>dotnet run [--max-queries N]</c>, with
/// <see cref="BeirDatasetCache.CacheDirectoryVariable"/> pointing at the BEIR cache and
/// <c>OPENROUTER_API_KEY</c> holding the key. The key is read from the environment and never
/// logged. <c>--max-queries</c> exists for the smoke run: verify the plumbing on a handful of
/// queries before spending the full budget, because the prompt template is part of the cache key
/// and a wrong template makes every entry garbage that is never hit again.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// The model identity hashed into every cache key, and the exact model string sent to
    /// OpenRouter — one constant so the two cannot drift. A later model change therefore misses
    /// rather than silently reusing another model's prose.
    /// </summary>
    internal const string ModelIdentity = "openai/gpt-4o-mini";

    private const string ApiKeyVariable = "OPENROUTER_API_KEY";

    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1");

    /// <summary>
    /// Temperature 0 — the provider's nearest equivalent of determinism, per the phase plan. This
    /// deliberately diverges from <see cref="HydeOptions.HypothesisTemperature"/>'s 0.8 default,
    /// which exists to diversify the hypotheses being averaged; at 0 the three per query may be
    /// near-duplicates. Recorded here because whoever changes it must also accept regenerating
    /// everything: the setting shapes the frozen text even though it is not part of the cache key.
    /// </summary>
    private const float Temperature = 0f;

    private const int MaxAttempts = 5;

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(2);

    private static readonly ChatOptions GenerationOptions = new() { Temperature = Temperature };

    public static async Task<int> Main(string[] args)
    {
        var maxQueries = ParseMaxQueries(args);
        if (maxQueries is null)
        {
            await Console.Error.WriteLineAsync(
                "Usage: Rag.NET.Benchmarks.Quality.Hypotheticals [--max-queries N]").ConfigureAwait(false);
            return 2;
        }

        var cacheRoot = BeirDatasetCache.ResolveCacheDirectoryFromEnvironment();
        if (cacheRoot is null)
        {
            await Console.Error.WriteLineAsync(
                $"Set {BeirDatasetCache.CacheDirectoryVariable} to the directory the BEIR datasets " +
                "and the hypothetical cache live in.").ConfigureAwait(false);
            return 2;
        }

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await Console.Error.WriteLineAsync(
                $"Set {ApiKeyVariable} to an OpenRouter API key. It is read from the environment " +
                "and never logged.").ConfigureAwait(false);
            return 2;
        }

        // The library's own defaults, deliberately: the table measures what the library does, so
        // the prompt template in every cache key is the one LlmHypotheticalDocumentGenerator uses.
        var options = new HydeOptions();
        var datasets = await LoadEvaluatedQueriesAsync(cacheRoot).ConfigureAwait(false);
        PrintPlan(datasets, options.HypothesisCount, maxQueries.Value);

        var cache = new HypotheticalCache(
            cacheRoot, ModelIdentity, options.PromptTemplate, HypotheticalCacheMode.Fill);
        using var chatClient = CreateChatClient(apiKey);

        var counters = new Counters();
        foreach (var dataset in datasets)
        {
            await GenerateForDatasetAsync(
                dataset, cache, chatClient, options, counters, maxQueries.Value).ConfigureAwait(false);
        }

        PrintSummary(counters);
        return counters.FailedQueries == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>--max-queries N</c> or nothing; anything else is <see langword="null"/>, which prints
    /// usage. The limit counts queries <i>visited</i> rather than generated, so a re-run of the
    /// same limit revisits the same queries and reports them all cached — the resume check.
    /// </summary>
    private static int? ParseMaxQueries(string[] args)
    {
        if (args.Length == 0)
        {
            return int.MaxValue;
        }

        if (args.Length == 2
            && string.Equals(args[0], "--max-queries", StringComparison.Ordinal)
            && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value > 0)
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Loads each dataset's queries and test qrels, keeping the queries that are judged — the only
    /// ones the table evaluates, so the only ones worth paying to generate hypotheticals for.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// A dataset's evaluated-query count disagrees with its descriptor, which would mean filling
    /// the cache for the wrong query set.
    /// </exception>
    private static async Task<IReadOnlyList<DatasetQueries>> LoadEvaluatedQueriesAsync(string cacheRoot)
    {
        var datasetCache = new BeirDatasetCache(cacheRoot);
        var datasets = new List<DatasetQueries>(BeirDatasetDescriptor.All.Count);

        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            var directory = await datasetCache.EnsureAsync(descriptor).ConfigureAwait(false);
            var queries = BeirLoader.LoadQueries(Path.Combine(directory, "queries.jsonl"));
            var qrels = BeirLoader.LoadQrels(Path.Combine(directory, "qrels", "test.tsv"));

            var evaluated = new List<BeirQuery>(descriptor.TestQueryCount);
            foreach (var query in queries)
            {
                if (qrels.ContainsKey(query.Id))
                {
                    evaluated.Add(query);
                }
            }

            if (evaluated.Count != descriptor.TestQueryCount)
            {
                throw new InvalidDataException(FormattableString.Invariant(
                    $"{descriptor.Name}: found {evaluated.Count} evaluated queries but the descriptor records {descriptor.TestQueryCount} distinct query ids in qrels/test.tsv. Generating from this listing would fill the cache for the wrong query set, at real cost."));
            }

            datasets.Add(new DatasetQueries(descriptor, evaluated));
        }

        return datasets;
    }

    private static void PrintPlan(IReadOnlyList<DatasetQueries> datasets, int hypothesisCount, int maxQueries)
    {
        var total = 0;
        foreach (var dataset in datasets)
        {
            total += dataset.Evaluated.Count;
            Console.WriteLine(FormattableString.Invariant(
                $"{dataset.Descriptor.Name}: {dataset.Evaluated.Count} evaluated queries (descriptor records {dataset.Descriptor.TestQueryCount})"));
        }

        Console.WriteLine(FormattableString.Invariant(
            $"{total} evaluated queries -> {total * hypothesisCount} hypotheticals at HypothesisCount = {hypothesisCount}, model {ModelIdentity}, temperature {Temperature}."));

        if (maxQueries != int.MaxValue)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"Limited to the first {maxQueries} queries this run (--max-queries)."));
        }
    }

    private static IChatClient CreateChatClient(string apiKey) =>
        new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = OpenRouterEndpoint })
            .GetChatClient(ModelIdentity)
            .AsIChatClient();

    /// <summary>
    /// Generates every missing hypothetical for one dataset, skipping entries already on disk.
    /// A query whose generation fails after every retry is reported and skipped rather than
    /// aborting the run: its already-cached hypotheses are untouched, and a re-run resumes at
    /// exactly the missing keys.
    /// </summary>
    private static async Task GenerateForDatasetAsync(
        DatasetQueries dataset,
        HypotheticalCache cache,
        IChatClient chatClient,
        HydeOptions options,
        Counters counters,
        int maxQueries)
    {
        var index = 0;
        foreach (var query in dataset.Evaluated)
        {
            if (counters.QueriesVisited >= maxQueries)
            {
                return;
            }

            counters.QueriesVisited++;
            index++;
            var generatedBefore = counters.Generated;

            try
            {
                await GenerateForQueryAsync(cache, chatClient, options, query.Text, counters)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                counters.FailedQueries++;
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"[{dataset.Descriptor.Name} {index}/{dataset.Evaluated.Count}] query '{query.Id}' failed after {MaxAttempts} attempts; its cached hypotheses are untouched and a re-run resumes at the missing keys. {ex}"));
                continue;
            }

            var generatedForQuery = counters.Generated - generatedBefore;
            if (generatedForQuery > 0 || index % 50 == 0 || index == dataset.Evaluated.Count)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"[{dataset.Descriptor.Name} {index}/{dataset.Evaluated.Count}] query '{query.Id}': {generatedForQuery} generated (totals: {counters.Generated} generated, {counters.Skipped} already cached, {counters.FailedQueries} failed)"));
            }
        }
    }

    /// <summary>
    /// One query's hypotheses. The prompt is built exactly as
    /// <c>LlmHypotheticalDocumentGenerator</c> builds it — the template with <c>{query}</c>
    /// replaced ordinally — and the missing indexes are generated concurrently, mirroring the
    /// library's parallel hypothesis calls.
    /// </summary>
    private static async Task GenerateForQueryAsync(
        HypotheticalCache cache,
        IChatClient chatClient,
        HydeOptions options,
        string queryText,
        Counters counters)
    {
        var prompt = options.PromptTemplate.Replace("{query}", queryText, StringComparison.Ordinal);

        var pending = new List<Task>(options.HypothesisCount);
        for (var hypothesisIndex = 0; hypothesisIndex < options.HypothesisCount; hypothesisIndex++)
        {
            if (cache.Contains(queryText, hypothesisIndex))
            {
                counters.RecordSkipped();
                continue;
            }

            pending.Add(GenerateOneAsync(cache, chatClient, prompt, queryText, hypothesisIndex, counters));
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private static async Task GenerateOneAsync(
        HypotheticalCache cache,
        IChatClient chatClient,
        string prompt,
        string queryText,
        int hypothesisIndex,
        Counters counters)
    {
        _ = await cache.GetOrAddAsync(
            queryText,
            hypothesisIndex,
            cancellationToken => GenerateWithRetryAsync(chatClient, prompt, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
        counters.RecordGenerated();
    }

    /// <summary>
    /// One generation call with retries. A transient failure — or a blank response, which the
    /// cache would refuse anyway — is retried with doubling delays; only after
    /// <see cref="MaxAttempts"/> does the failure propagate, and nothing is ever written on a
    /// failure path, so an error string cannot become a cached hypothetical.
    /// </summary>
    private static async Task<string> GenerateWithRetryAsync(
        IChatClient chatClient, string prompt, CancellationToken cancellationToken)
    {
        var delay = FirstRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await chatClient
                    .GetResponseAsync(prompt, GenerationOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response.Text))
                {
                    throw new InvalidOperationException(
                        "The model returned blank text; retrying rather than caching it.");
                }

                return response.Text;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"  attempt {attempt}/{MaxAttempts} failed, retrying in {delay.TotalSeconds:F0}s: {ex}"));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    private static void PrintSummary(Counters counters)
    {
        Console.WriteLine(FormattableString.Invariant(
            $"Done: {counters.Generated} generated, {counters.Skipped} already cached, {counters.FailedQueries} queries failed, over {counters.QueriesVisited} queries visited."));

        if (counters.FailedQueries > 0)
        {
            Console.WriteLine(
                "Failures left gaps, not corruption: re-run this tool and it resumes at exactly " +
                "the missing keys.");
        }
    }

    /// <summary>One dataset and the subset of its queries that qrels judge.</summary>
    private sealed record DatasetQueries(
        BeirDatasetDescriptor Descriptor, IReadOnlyList<BeirQuery> Evaluated);

    /// <summary>
    /// Run totals. Generated and skipped counts are incremented from a query's concurrent
    /// hypothesis tasks, so those two are interlocked; the query-level counts only ever move on
    /// the single-threaded outer loop.
    /// </summary>
    private sealed class Counters
    {
        private long _generated;
        private long _skipped;

        public long Generated => Interlocked.Read(ref _generated);

        public long Skipped => Interlocked.Read(ref _skipped);

        public int QueriesVisited { get; set; }

        public int FailedQueries { get; set; }

        public void RecordGenerated() => _ = Interlocked.Increment(ref _generated);

        public void RecordSkipped() => _ = Interlocked.Increment(ref _skipped);
    }
}
