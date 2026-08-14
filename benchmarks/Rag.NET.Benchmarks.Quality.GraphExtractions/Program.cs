using System.ClientModel;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenAI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;
using Rag.NET.Models;

namespace Rag.NET.Benchmarks.Quality.GraphExtractions;

/// <summary>
/// The GraphRAG extraction generation tool. Selects a corpus of MultiHop-RAG articles, chunks it
/// exactly as the guard will, drives <c>GraphEntityExtractionBehavior</c> over every chunk through
/// OpenRouter, and writes every response into <see cref="GraphExtractionCache"/>.
/// <para>
/// <b>The cached text is the experiment.</b> Hosted LLMs are not bit-deterministic even at
/// temperature 0, so the GraphRAG guard never calls a model: it reads what this tool wrote,
/// verbatim, forever. That is also why this tool is resumable — every response already on disk is a
/// hit and costs nothing — so an interrupted run continues rather than regenerating text it can
/// never reproduce.
/// </para>
/// <para>
/// <b>Two corpora, and the default is the one that is already paid for.</b>
/// <c>--corpus slice</c> — the default, and what an invocation with no arguments has always meant —
/// is the pinned sixty-article slice <c>GraphRagFunctionsTests</c> reads, guarded so that a walk
/// landing anywhere but exactly sixty is refused rather than extracted. <c>--corpus full</c> is
/// every article in the corpus: roughly 41,000 requests, hours of wall clock, and real money. The
/// slice is a subset of the full corpus and chunks identically inside it, so a full run re-uses
/// every extraction the slice already bought — <see cref="PrintPlanAsync"/> counts exactly how many
/// before anything is spent.
/// </para>
/// <para>
/// Usage: <c>dotnet run [--corpus slice|full] [--max-documents N]</c>, with
/// <see cref="BeirDatasetCache.CacheDirectoryVariable"/> pointing at the BEIR cache and
/// <c>OPENROUTER_API_KEY</c> holding the key. The key is read from the environment and never
/// logged. <c>--max-documents</c> bounds either corpus and exists for the smoke run: verify the
/// plumbing on two or three articles before spending the whole budget.
/// </para>
/// <para>
/// <b>Documents run concurrently, chunks within a document do not.</b> The gleaning pass's prompt
/// embeds the previous extraction, so a document's two calls per chunk are inherently sequential;
/// articles are independent and there are dozens to hundreds of them. Each concurrent article gets
/// a throwaway in-memory graph store, because <c>SqliteGraphStore</c> holds one connection and is
/// not thread-safe — and because nothing this tool extracts is kept. The guard rebuilds the graph
/// from the cache, into one store, sequentially.
/// </para>
/// </summary>
internal static class Program
{
    private const string ApiKeyVariable = "OPENROUTER_API_KEY";

    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1");

    /// <summary>How many articles are extracted at once.</summary>
    /// <remarks>
    /// Sixty articles at roughly twenty chunks each and two calls per chunk is a few thousand
    /// sequential requests, which is hours. Twelve at a time is minutes and stays well inside
    /// OpenRouter's per-key concurrency for this model; the retry in
    /// <see cref="CachedGraphExtractionClient"/> absorbs the rate-limit responses that do arrive.
    /// </remarks>
    private const int Concurrency = 12;

    public static async Task<int> Main(string[] args)
    {
        var options = GraphExtractionRunOptions.Parse(args);
        if (options is null)
        {
            await Console.Error.WriteLineAsync(GraphExtractionRunOptions.Usage);
            return 2;
        }

        var cacheRoot = BeirDatasetCache.ResolveCacheDirectoryFromEnvironment();
        if (cacheRoot is null)
        {
            await Console.Error.WriteLineAsync(
                $"Set {BeirDatasetCache.CacheDirectoryVariable} to the directory the BEIR datasets " +
                "and the extraction cache live in.");
            return 2;
        }

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await Console.Error.WriteLineAsync(
                $"Set {ApiKeyVariable} to an OpenRouter API key. It is read from the environment " +
                "and never logged.");
            return 2;
        }

        return await RunAsync(cacheRoot, apiKey, options);
    }

    /// <summary>Loads the corpus, fills the cache, and reports what it cost.</summary>
    private static async Task<int> RunAsync(
        string cacheRoot, string apiKey, GraphExtractionRunOptions options)
    {
        var selection = await LoadCorpusAsync(cacheRoot, options);
        var chunked = await ChunkAsync(selection.Documents);

        var cache = new GraphExtractionCache(
            cacheRoot,
            GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature),
            GraphExtractionCacheMode.Fill);

        using var model = CreateChatClient(apiKey);
        using var client = new CachedGraphExtractionClient(
            cache, model, GraphExtractionModelIdentity.ExtractionTemperature);

        await PrintPlanAsync(cache, selection, chunked);

        var startedAt = Stopwatch.GetTimestamp();
        await ExtractAllAsync(chunked, client, (completed, document) =>
            Report(completed, chunked.Count, client, document));
        await DescribeTheGraphAsync(chunked, client);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        PrintSummary(client, chunked, elapsed);
        return 0;
    }

    /// <summary>
    /// Rebuilds the whole run's graph into one store — every extraction a cache hit by now — and
    /// reports what it contains, including <b>what a community report would cost to generate</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No LLM calls, and no community reports are cached.</b> That was this phase's original
    /// intent and it does not survive contact with the numbers this prints. The report prompt is
    /// built by pasting every member entity's whole merged description into one message, with no
    /// bound of any kind — and Leiden puts most of this graph in one community, so that one
    /// prompt runs to millions of characters. There is no model to send it to. The guard therefore
    /// synthesises reports deterministically instead of generating them, and says so; this printout
    /// is the evidence for that decision, and the thing to re-read if the report prompt ever grows
    /// a bound.
    /// </para>
    /// <para>
    /// The rebuild is sequential and in corpus order, which matters: entity descriptions merge by
    /// concatenation, so ingestion order decides what every merged description says. The guard
    /// projects the slice the same way.
    /// </para>
    /// </remarks>
    private static async Task DescribeTheGraphAsync(
        IReadOnlyList<ChunkedDocument> chunked, CachedGraphExtractionClient client)
    {
        var options = GraphRagSliceIngestion.CreateOptions();
        await using var graphStore = new SqliteGraphStore(":memory:");
        using var embedder = new StubEmbeddingGenerator();

        for (var i = 0; i < chunked.Count; i++)
        {
            _ = await GraphRagSliceIngestion.ExtractAsync(
                chunked[i].Document, chunked[i].Chunks, client, embedder, graphStore, options,
                CancellationToken.None);
        }

        var snapshot = await graphStore.GetFullGraphAsync();
        PrintGraph(snapshot, Leiden.Detect(snapshot));
    }

    /// <summary>Prints the graph's shape and the community-report prompt sizes it implies.</summary>
    private static void PrintGraph(GraphSnapshot snapshot, IReadOnlyList<Community> communities)
    {
        var singletons = 0;
        var largest = 0;
        var longestPrompt = 0L;
        var totalPrompt = 0L;

        var descriptions = new Dictionary<string, int>(snapshot.Entities.Count, StringComparer.Ordinal);
        for (var i = 0; i < snapshot.Entities.Count; i++)
        {
            descriptions[snapshot.Entities[i].Name] = snapshot.Entities[i].Description.Length;
        }

        for (var i = 0; i < communities.Count; i++)
        {
            var members = communities[i].MemberEntities;
            singletons += members.Count <= 1 ? 1 : 0;
            largest = Math.Max(largest, members.Count);

            var prompt = 0L;
            for (var j = 0; j < members.Count; j++)
            {
                prompt += descriptions.GetValueOrDefault(members[j]) + members[j].Length;
            }

            longestPrompt = Math.Max(longestPrompt, prompt);
            totalPrompt += prompt;
        }

        Console.WriteLine(FormattableString.Invariant(
            $"Graph: {snapshot.Entities.Count} entities, {snapshot.Relationships.Count} relationships, {communities.Count} communities ({singletons} singletons, largest holds {largest})."));
        Console.WriteLine(FormattableString.Invariant(
            $"Community report prompts: {totalPrompt} characters in total, longest {longestPrompt} — entity descriptions alone, before the relationship block and the instructions."));
    }

    /// <summary>Runs every article's extraction, <see cref="Concurrency"/> at a time.</summary>
    /// <param name="chunked">The articles and their chunks.</param>
    /// <param name="client">
    /// What answers each request: the caching client for the run that pays, or
    /// <see cref="GraphExtractionPlanProbe"/> for the dry pass that costs it first. One method for
    /// both, because a plan produced by a second walk over the chunks would be a plan for a
    /// different run than the one about to start.
    /// </param>
    /// <param name="report">Called as each article finishes, or <see langword="null"/> to say nothing.</param>
    private static Task ExtractAllAsync(
        IReadOnlyList<ChunkedDocument> chunked,
        IChatClient client,
        Action<int, ChunkedDocument>? report)
    {
        var options = GraphRagSliceIngestion.CreateOptions();
        var completed = 0;

        return Parallel.ForEachAsync(
            chunked,
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency },
            async (document, cancellationToken) =>
            {
                await using var graphStore = new SqliteGraphStore(":memory:");
                using var embedder = new StubEmbeddingGenerator();

                _ = await GraphRagSliceIngestion.ExtractAsync(
                    document.Document, document.Chunks, client, embedder, graphStore, options,
                    cancellationToken);

                report?.Invoke(Interlocked.Increment(ref completed), document);
            });
    }

    /// <summary>Prints one article's completion, with the running cache tallies.</summary>
    private static void Report(
        int completed, int total, CachedGraphExtractionClient client, ChunkedDocument document) =>
        Console.WriteLine(FormattableString.Invariant(
            $"[{completed}/{total}] {document.Chunks.Count} chunks — {document.Document.Id} (totals: {client.Cache.Hits} cached, {client.Cache.Misses} generated)"));

    /// <summary>
    /// Loads the whole converted dataset and takes the articles the chosen corpus names.
    /// </summary>
    /// <remarks>
    /// <b>The dataset was always loaded whole.</b> <c>BeirLoader.Load</c> reads every article the
    /// conversion wrote; what used to happen next was an unconditional filter down to the slice.
    /// The selection below is that filter made optional — same load, same order, same chunking —
    /// which is why the slice's cached extractions are still cache hits inside a full run.
    /// </remarks>
    /// <exception cref="InvalidDataException">The slice walk did not reach its target.</exception>
    private static async Task<GraphExtractionCorpusSelection> LoadCorpusAsync(
        string cacheRoot, GraphExtractionRunOptions options)
    {
        var descriptor = BeirDatasetDescriptor.ByName(MultiHopRagSource.DatasetName);
        var directory = await new BeirDatasetCache(cacheRoot).EnsureAsync(descriptor);
        var dataset = BeirLoader.Load(directory, "test", BeirLoader.DefaultTitleTextSeparator);

        var selection = GraphExtractionCorpusSelection.Select(
            dataset, options.Corpus, options.MaxDocuments);

        Console.WriteLine(selection.Describe());
        return selection;
    }

    /// <summary>Chunks every article up front, so the plan can state what the run will cost.</summary>
    private static async Task<IReadOnlyList<ChunkedDocument>> ChunkAsync(
        IReadOnlyList<BeirDocument> documents)
    {
        var chunked = new List<ChunkedDocument>(documents.Count);
        for (var i = 0; i < documents.Count; i++)
        {
            chunked.Add(new ChunkedDocument(
                documents[i],
                await GraphRagSliceIngestion.ChunkAsync(documents[i], CancellationToken.None)));
        }

        return chunked;
    }

    /// <summary>
    /// States what the run will do <b>before it does any of it</b>: how many articles, how many
    /// chunks, how many requests that implies, and how many of those requests are already on disk
    /// and therefore free.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The last of those four is the one that decides anything.</b> A full-corpus run is hours
    /// of wall clock and a bill, and most of the difference between "this costs twelve dollars" and
    /// "this costs ten" is how much of the corpus a previous run already bought — the sixty slice
    /// articles chunk identically inside the full corpus, so their extractions are hits. Printing
    /// the estimate after the spend would be a receipt; printing it here is a decision.
    /// </para>
    /// <para>
    /// It is counted rather than derived, by replaying the whole extraction against
    /// <see cref="GraphExtractionPlanProbe"/>, which calls no model. Only the first request of each
    /// chunk has a key that can be computed up front; the gleaning follow-up's prompt embeds the
    /// previous extraction, so anything short of walking the chain would be guessing at half of the
    /// requests.
    /// </para>
    /// </remarks>
    private static async Task PrintPlanAsync(
        GraphExtractionCache cache,
        GraphExtractionCorpusSelection selection,
        IReadOnlyList<ChunkedDocument> chunked)
    {
        var chunks = CountChunks(chunked);
        var callsPerChunk = 1 + GraphRagSliceIngestion.CreateOptions().GleaningPasses;
        var requests = (long)chunks * callsPerChunk;

        Console.WriteLine(FormattableString.Invariant(
            $"Plan: {chunked.Count} articles, {chunks} chunks, {callsPerChunk} calls per chunk = {requests} requests at most."));
        Console.WriteLine(FormattableString.Invariant(
            $"Costing the plan against the cache — {selection.Corpus} corpus, no model calls…"));

        var (cached, uncached) = await ProbeCacheAsync(cache, chunked);
        Console.WriteLine(FormattableString.Invariant(
            $"Already cached: {cached} of {cached + uncached} requests. This run generates {uncached} and pays for those only."));
        Console.WriteLine(FormattableString.Invariant(
            $"Cache identity {cache.ModelIdentity}, entries in {cache.EntryDirectory}."));
    }

    /// <summary>Replays the whole extraction against the cache alone, counting hits and misses.</summary>
    private static async Task<(long Cached, long Uncached)> ProbeCacheAsync(
        GraphExtractionCache cache, IReadOnlyList<ChunkedDocument> chunked)
    {
        using var probe = new GraphExtractionPlanProbe(cache);
        await ExtractAllAsync(chunked, probe, report: null);

        return (probe.Cached, probe.Uncached);
    }

    /// <summary>How many chunks the selected articles were cut into, in total.</summary>
    private static int CountChunks(IReadOnlyList<ChunkedDocument> chunked)
    {
        var chunks = 0;
        for (var i = 0; i < chunked.Count; i++)
        {
            chunks += chunked[i].Chunks.Count;
        }

        return chunks;
    }

    private static void PrintSummary(
        CachedGraphExtractionClient client, IReadOnlyList<ChunkedDocument> chunked, TimeSpan elapsed)
    {
        Console.WriteLine(FormattableString.Invariant(
            $"Done: {client.Calls} requests over {chunked.Count} articles — {client.Cache.Hits} served from cache, {client.Cache.Misses} generated — in {elapsed.TotalSeconds:F1} s."));
    }

    private static IChatClient CreateChatClient(string apiKey) =>
        new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = OpenRouterEndpoint })
            .GetChatClient(GraphExtractionModelIdentity.ModelName)
            .AsIChatClient();

    /// <summary>One article and the chunks the library's default strategy cut it into.</summary>
    private sealed record ChunkedDocument(BeirDocument Document, IReadOnlyList<TextChunk> Chunks);
}
