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
/// <b>The cached text is the experiment.</b> Hosted LLMs are not bit-deterministic at any
/// temperature, so the ablation table's HyDE row never calls an LLM: it reads what this tool wrote,
/// verbatim, forever. That is also why this tool is resumable — every entry already on disk is
/// skipped, so an interrupted run continues instead of regenerating text it can never reproduce.
/// </para>
/// <para>
/// <b>Determinism is the cache's job, not the sampler's</b>, which is why generation runs at
/// <see cref="HydeOptions.HypothesisTemperature"/>'s 0.8 rather than at 0. Once the text is on disk
/// the experiment is frozen however it was sampled; turning the temperature down would only have
/// made the three hypotheses per query near-identical, collapsing the mean-of-3 that
/// <see cref="HydeOptions.HypothesisCount"/> exists for into a mean-of-1 nobody ships.
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
    private const string ApiKeyVariable = "OPENROUTER_API_KEY";

    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1");

    private const int MaxAttempts = 5;

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The identity hashed into every cache key. <b>Not defined here</b>: it lives in
    /// <see cref="HypotheticalModelIdentity"/>, beside <see cref="HypotheticalCache"/>, because the
    /// ablation table's HyDE row computes the very same string to read what this tool wrote — two
    /// hand-maintained copies would be a drift waiting to orphan the whole cache.
    /// </summary>
    internal static string BuildModelIdentity(HydeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return HypotheticalModelIdentity.For(options.HypothesisTemperature);
    }

    public static async Task<int> Main(string[] args)
    {
        var arguments = ParseArguments(args);
        if (arguments is null)
        {
            await Console.Error.WriteLineAsync(
                "Usage: Rag.NET.Benchmarks.Quality.Hypotheticals [--dataset NAME] [--max-queries N]")
                .ConfigureAwait(false);
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

        // The library's own defaults throughout — prompt template, hypothesis count AND sampling
        // temperature — because the table measures what the library does. Temperature 0 was the
        // first version of this and it was a mistake worth recording: determinism comes from the
        // cache, not from the sampler, so 0 bought nothing and cost the variance that
        // HypothesisCount = 3 exists to average away. At 0 the three hypotheses per query come back
        // near-identical and mean-of-3 collapses toward mean-of-1 — a configuration nobody ships.
        var options = new HydeOptions();
        var datasets = await LoadEvaluatedQueriesAsync(cacheRoot, arguments.DatasetName).ConfigureAwait(false);
        PrintPlan(datasets, options, arguments.MaxQueries);

        var cache = new HypotheticalCache(
            cacheRoot, BuildModelIdentity(options), options.PromptTemplate, HypotheticalCacheMode.Fill);
        using var chatClient = CreateChatClient(apiKey);

        var counters = new Counters();
        foreach (var dataset in datasets)
        {
            await GenerateForDatasetAsync(
                dataset, cache, chatClient, options, counters, arguments.MaxQueries).ConfigureAwait(false);
        }

        PrintSummary(counters);
        return counters.FailedQueries == 0 ? 0 : 1;
    }

    /// <summary>
    /// Parses <c>--dataset NAME</c> and <c>--max-queries N</c>, in either order; anything else is
    /// <see langword="null"/>, which prints usage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--max-queries</c> counts queries <i>visited</i>, not generated, so re-running the same
    /// limit revisits the same queries and reports them all cached — the resume check.
    /// </para>
    /// <para>
    /// <c>--dataset</c> exists because the datasets are walked in descriptor order and ArguAna is
    /// 948 queries down: without a filter there is no way to sample it, or to run the generation in
    /// slices. Slicing is free — every already-cached index is skipped on the way past.
    /// </para>
    /// </remarks>
    private static RunArguments? ParseArguments(string[] args)
    {
        string? datasetName = null;
        var maxQueries = int.MaxValue;

        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                return null;
            }

            var value = args[i + 1];
            if (string.Equals(args[i], "--dataset", StringComparison.Ordinal))
            {
                if (!KnowsDataset(value))
                {
                    return null;
                }

                datasetName = value;
            }
            else if (string.Equals(args[i], "--max-queries", StringComparison.Ordinal))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out maxQueries)
                    || maxQueries <= 0)
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        return new RunArguments(datasetName, maxQueries);
    }

    private static bool KnowsDataset(string name)
    {
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            if (string.Equals(descriptor.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Loads each dataset's queries and test qrels, keeping the queries that are judged — the only
    /// ones the table evaluates, so the only ones worth paying to generate hypotheticals for.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// A dataset's evaluated-query count disagrees with its descriptor, which would mean filling
    /// the cache for the wrong query set.
    /// </exception>
    private static async Task<IReadOnlyList<DatasetQueries>> LoadEvaluatedQueriesAsync(
        string cacheRoot, string? datasetName)
    {
        var datasetCache = new BeirDatasetCache(cacheRoot);
        var datasets = new List<DatasetQueries>(BeirDatasetDescriptor.All.Count);

        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            if (datasetName is not null
                && !string.Equals(descriptor.Name, datasetName, StringComparison.Ordinal))
            {
                continue;
            }

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

    private static void PrintPlan(IReadOnlyList<DatasetQueries> datasets, HydeOptions options, int maxQueries)
    {
        var total = 0;
        foreach (var dataset in datasets)
        {
            total += dataset.Evaluated.Count;
            Console.WriteLine(FormattableString.Invariant(
                $"{dataset.Descriptor.Name}: {dataset.Evaluated.Count} evaluated queries (descriptor records {dataset.Descriptor.TestQueryCount})"));
        }

        Console.WriteLine(FormattableString.Invariant(
            $"{total} evaluated queries -> {total * options.HypothesisCount} hypotheticals at HypothesisCount = {options.HypothesisCount}, cache identity {BuildModelIdentity(options)}."));

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
            .GetChatClient(HypotheticalModelIdentity.ModelName)
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

        // The library's temperature, and the reason the three calls below are worth making at all:
        // at 0.8 they sample three different passages, which is what averaging smooths.
        var chatOptions = new ChatOptions { Temperature = options.HypothesisTemperature };

        var pending = new List<Task>(options.HypothesisCount);
        for (var hypothesisIndex = 0; hypothesisIndex < options.HypothesisCount; hypothesisIndex++)
        {
            if (cache.Contains(queryText, hypothesisIndex))
            {
                counters.RecordSkipped();
                continue;
            }

            pending.Add(GenerateOneAsync(
                cache, chatClient, prompt, chatOptions, queryText, hypothesisIndex, counters));
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private static async Task GenerateOneAsync(
        HypotheticalCache cache,
        IChatClient chatClient,
        string prompt,
        ChatOptions chatOptions,
        string queryText,
        int hypothesisIndex,
        Counters counters)
    {
        _ = await cache.GetOrAddAsync(
            queryText,
            hypothesisIndex,
            cancellationToken => GenerateWithRetryAsync(chatClient, prompt, chatOptions, cancellationToken),
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
        IChatClient chatClient, string prompt, ChatOptions chatOptions, CancellationToken cancellationToken)
    {
        var delay = FirstRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await chatClient
                    .GetResponseAsync(prompt, chatOptions, cancellationToken)
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

    /// <summary>The command line: which dataset to walk, and how many of its queries to visit.</summary>
    private sealed record RunArguments(string? DatasetName, int MaxQueries);

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
