using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.AI;
using OpenAI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;
using Rag.NET.Models;

namespace Rag.NET.Benchmarks.Quality.GraphExtractions;

/// <summary>
/// The one-time GraphRAG extraction generation tool. Derives the sixty-article MultiHop-RAG slice,
/// chunks it exactly as the guard will, drives <c>GraphEntityExtractionBehavior</c> over every
/// chunk through OpenRouter, and writes every response into <see cref="GraphExtractionCache"/>.
/// <para>
/// <b>The cached text is the experiment.</b> Hosted LLMs are not bit-deterministic even at
/// temperature 0, so the GraphRAG guard never calls a model: it reads what this tool wrote,
/// verbatim, forever. That is also why this tool is resumable — every response already on disk is a
/// hit and costs nothing — so an interrupted run continues rather than regenerating text it can
/// never reproduce.
/// </para>
/// <para>
/// Usage: <c>dotnet run [--max-documents N]</c>, with
/// <see cref="BeirDatasetCache.CacheDirectoryVariable"/> pointing at the BEIR cache and
/// <c>OPENROUTER_API_KEY</c> holding the key. The key is read from the environment and never
/// logged. <c>--max-documents</c> exists for the smoke run: verify the plumbing on two or three
/// articles before spending the whole budget.
/// </para>
/// <para>
/// <b>Documents run concurrently, chunks within a document do not.</b> The gleaning pass's prompt
/// embeds the previous extraction, so a document's two calls per chunk are inherently sequential;
/// articles are independent and there are sixty of them. Each concurrent article gets a throwaway
/// in-memory graph store, because <c>SqliteGraphStore</c> holds one connection and is not
/// thread-safe — and because nothing this tool extracts is kept. The guard rebuilds the graph from
/// the cache, into one store, sequentially.
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
        var maxDocuments = ParseMaxDocuments(args);
        if (maxDocuments is null)
        {
            await Console.Error.WriteLineAsync(
                "Usage: Rag.NET.Benchmarks.Quality.GraphExtractions [--max-documents N]");
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

        return await RunAsync(cacheRoot, apiKey, maxDocuments.Value);
    }

    /// <summary>Loads the slice, fills the cache, and reports what it cost.</summary>
    private static async Task<int> RunAsync(string cacheRoot, string apiKey, int maxDocuments)
    {
        var documents = await LoadSliceAsync(cacheRoot, maxDocuments);
        var chunked = await ChunkAsync(documents);

        var cache = new GraphExtractionCache(
            cacheRoot,
            GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature),
            GraphExtractionCacheMode.Fill);

        using var model = CreateChatClient(apiKey);
        using var client = new CachedGraphExtractionClient(
            cache, model, GraphExtractionModelIdentity.ExtractionTemperature);

        PrintPlan(cache, chunked);

        var startedAt = Stopwatch.GetTimestamp();
        await ExtractAllAsync(chunked, client);
        await DescribeTheGraphAsync(chunked, client);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        PrintSummary(client, chunked, elapsed);
        return 0;
    }

    /// <summary>
    /// Rebuilds the whole slice's graph into one store — every extraction a cache hit by now — and
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
    private static Task ExtractAllAsync(
        IReadOnlyList<ChunkedDocument> chunked, CachedGraphExtractionClient client)
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

                Report(Interlocked.Increment(ref completed), chunked.Count, client, document);
            });
    }

    /// <summary>Prints one article's completion, with the running cache tallies.</summary>
    private static void Report(
        int completed, int total, CachedGraphExtractionClient client, ChunkedDocument document) =>
        Console.WriteLine(FormattableString.Invariant(
            $"[{completed}/{total}] {document.Chunks.Count} chunks — {document.Document.Id} (totals: {client.Cache.Hits} cached, {client.Cache.Misses} generated)"));

    /// <summary>Derives the slice from the converted dataset and takes the documents it names.</summary>
    /// <exception cref="InvalidDataException">The walk did not reach its target.</exception>
    private static async Task<IReadOnlyList<BeirDocument>> LoadSliceAsync(
        string cacheRoot, int maxDocuments)
    {
        var descriptor = BeirDatasetDescriptor.ByName(MultiHopRagSource.DatasetName);
        var directory = await new BeirDatasetCache(cacheRoot).EnsureAsync(descriptor);
        var dataset = BeirLoader.Load(directory, "test", BeirLoader.DefaultTitleTextSeparator);

        var (queryIds, documentIds) = MultiHopRagSliceWalk.Derive(dataset);
        if (documentIds.Count != MultiHopRagSliceWalk.TargetDocumentCount)
        {
            throw new InvalidDataException(FormattableString.Invariant(
                $"The slice walk produced {documentIds.Count} articles, not {MultiHopRagSliceWalk.TargetDocumentCount}. Filling the cache for a different slice than the guard reads would cost real money and still miss on every key."));
        }

        Console.WriteLine(FormattableString.Invariant(
            $"Slice: {documentIds.Count} articles derived from {queryIds.Count} judged queries."));

        return TakeInCorpusOrder(dataset.Documents, documentIds, maxDocuments);
    }

    /// <summary>
    /// Projects the walked ids back to documents <b>in corpus order</b>, honouring the limit.
    /// </summary>
    /// <remarks>
    /// Corpus order rather than walk order, and the difference is load bearing for the community
    /// phase. Entities merge in <c>SqliteGraphStore</c> by concatenating descriptions, so the order
    /// documents are ingested in decides what every merged entity description says — which decides
    /// the community report prompts, which are cached. The guard projects the slice the same way
    /// (<c>MultiHopRagSlice.Documents</c> filters the corpus in place), so both build the identical
    /// graph. Walk order would be a second ordering that agrees today and could stop agreeing.
    /// </remarks>
    private static List<BeirDocument> TakeInCorpusOrder(
        IReadOnlyList<BeirDocument> corpus, IReadOnlyList<string> documentIds, int maxDocuments)
    {
        var wanted = new HashSet<string>(documentIds, StringComparer.Ordinal);
        var taken = new List<BeirDocument>(Math.Min(documentIds.Count, maxDocuments));

        for (var i = 0; i < corpus.Count && taken.Count < maxDocuments; i++)
        {
            if (wanted.Contains(corpus[i].Id))
            {
                taken.Add(corpus[i]);
            }
        }

        return taken;
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

    private static void PrintPlan(GraphExtractionCache cache, IReadOnlyList<ChunkedDocument> chunked)
    {
        var chunks = 0;
        for (var i = 0; i < chunked.Count; i++)
        {
            chunks += chunked[i].Chunks.Count;
        }

        var options = GraphRagSliceIngestion.CreateOptions();
        Console.WriteLine(FormattableString.Invariant(
            $"{chunked.Count} articles, {chunks} chunks, {1 + options.GleaningPasses} calls per chunk = {chunks * (1 + options.GleaningPasses)} requests at most."));
        Console.WriteLine(FormattableString.Invariant(
            $"Cache identity {cache.ModelIdentity}, entries in {cache.EntryDirectory}."));
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

    /// <summary>
    /// Parses <c>--max-documents N</c>; absent means every article, anything else is
    /// <see langword="null"/>, which prints usage.
    /// </summary>
    private static int? ParseMaxDocuments(string[] args)
    {
        if (args.Length == 0)
        {
            return int.MaxValue;
        }

        return args.Length == 2
            && string.Equals(args[0], "--max-documents", StringComparison.Ordinal)
            && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var max)
            && max > 0
                ? max
                : null;
    }

    /// <summary>One article and the chunks the library's default strategy cut it into.</summary>
    private sealed record ChunkedDocument(BeirDocument Document, IReadOnlyList<TextChunk> Chunks);
}
