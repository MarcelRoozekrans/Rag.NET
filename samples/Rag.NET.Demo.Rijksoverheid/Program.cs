// Rag.NET.Demo.Rijksoverheid — a proof of concept on a real public site: ingest the health theme
// of rijksoverheid.nl (Dutch government, texts published under CC0) from its sitemap, then answer
// Dutch questions with the source pages cited.
//
// Needs Docker (Postgres + pgvector via Testcontainers) and OPENAI_API_KEY. See README.md.
// Run with --eval to measure retrieval variants against eval/vragen.json instead of chatting.

using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Web;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;
using Rag.NET.PgVector;
using Rag.NET.QueryTechniques;
using Rag.NET.Reranking.Cohere;
using Testcontainers.PostgreSql;
using ZeroAlloc.Results;

const string SitemapUrl = "https://www.rijksoverheid.nl/sitemap.xml";
const string ThemeRoot = "https://www.rijksoverheid.nl/themas/familie-zorg-en-gezondheid";
const string ThemePrefix = ThemeRoot + "/";

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    await Console.Error.WriteLineAsync("Set the OPENAI_API_KEY environment variable.");
    return 1;
}

var chatModel = Environment.GetEnvironmentVariable("OPENAI_CHAT_MODEL") ?? "gpt-4o-mini";
var embeddingModel = Environment.GetEnvironmentVariable("OPENAI_EMBEDDING_MODEL") ?? "text-embedding-3-small";
const int VectorDimensions = 1536; // text-embedding-3-small
var evalMode = Array.Exists(args, a => string.Equals(a, "--eval", StringComparison.Ordinal));
var cohereKey = Environment.GetEnvironmentVariable("COHERE_API_KEY");
var rerankerRegistered = evalMode && !string.IsNullOrWhiteSpace(cohereKey);

// The chat demo caps the crawl so it starts quickly; the evaluation needs every page a question
// may point at, so it walks the whole theme unless told otherwise.
var maxPages = int.TryParse(Environment.GetEnvironmentVariable("RAG_MAX_PAGES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
    ? parsed
    : evalMode ? int.MaxValue : 150;

Console.WriteLine("Starting PostgreSQL (pgvector) container...");
var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();
await postgres.StartAsync();

try
{
    var services = new ServiceCollection();
    services.AddChatClient(new OpenAI.Chat.ChatClient(chatModel, apiKey).AsIChatClient());
    services.AddEmbeddingGenerator(
        new OpenAI.Embeddings.EmbeddingClient(embeddingModel, apiKey).AsIEmbeddingGenerator());
    services.AddRagNet(rag =>
    {
        rag.UsePgVector(postgres.GetConnectionString(), VectorDimensions)
           .AddHtmlParser();

        // Only the evaluation registers these, and it switches each on per variant. Registered in
        // chat mode they would apply to every question, because RetrievalOptions defaults
        // UseHyde and UseReranking to true.
        if (evalMode)
        {
            rag.UseHyde();
        }

        if (rerankerRegistered)
        {
            rag.UseCohereReranking(o =>
            {
                o.ApiKey = cohereKey!;
                // Multilingual; the package default is English-only.
                o.Model = Environment.GetEnvironmentVariable("COHERE_RERANK_MODEL") ?? "rerank-v3.5";
            });
        }
    });

    await using var serviceProvider = services.BuildServiceProvider();
    if (serviceProvider.GetRequiredService<IVectorStore>() is PgVectorStore store)
    {
        await store.InitializeAsync();
    }

    var pipeline = serviceProvider.GetRequiredService<IRagPipeline>();

    // --- Ingest: one theme of a ~120,000-URL site, politely ---
    using var httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Rag.NET-Demo/1.0 (+https://github.com/MarcelRoozekrans/Rag.NET)");

    // Two sources, because neither alone has every page. The site's FAQ answers
    // (…/vraag-en-antwoord/roken/is-meeroken-slecht-…) are missing from its sitemap and reachable
    // only by following links, so the crawler goes first; the sitemap then adds the pages no link
    // led to. Its shard walk is the slow part, so the capped chat demo rarely needs it.
    var crawler = new WebCrawlerDataProvider(ThemeRoot, httpClient, new WebCrawlerOptions
    {
        // Theme → sub-theme → FAQ index → FAQ answer.
        MaxDepth = 3,
        MaxPages = 5_000,
        IncludedUrlPrefixes = [ThemeRoot],
        RespectRobotsTxt = true,
        MinimumRequestInterval = TimeSpan.FromMilliseconds(500),
    });

    var sitemap = new SitemapDataProvider(SitemapUrl, httpClient, new SitemapOptions
    {
        // The site shards its index by number, so every shard is still read; only these pages
        // are kept from them.
        IncludedUrlPrefixes = [ThemePrefix],
        RespectRobotsTxt = true,
        // One request every half second: sitemaps, robots.txt and pages alike.
        MinimumRequestInterval = TimeSpan.FromMilliseconds(500),
    });

    Console.WriteLine(maxPages == int.MaxValue
        ? $"\nIngesting every page under {ThemePrefix}"
        : $"\nIngesting up to {maxPages} pages under {ThemePrefix}");
    Console.WriteLine("(two requests a second: minutes for the chat demo, longer for the full theme)\n");

    // Progress reports several times per page (one per stage and batch); print each page once.
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var progress = new Progress<IngestionProgress>(p =>
    {
        lock (seen)
        {
            if (p.Stage == IngestionProgressStage.Storing && seen.Add(p.DocumentId))
            {
                Console.WriteLine($"  [{seen.Count,3}] {p.DocumentId}");
            }
        }
    });

    var result = await pipeline.IngestFromProviderAsync(
        new FirstPages(new UniquePages(crawler, sitemap), maxPages),
        new ProviderId("rijksoverheid-gezondheid"),
        baseMetadata: new DocumentMetadata
        {
            DocumentId = new DocumentId("rijksoverheid"),
            FileName = "rijksoverheid",
            ContentType = "text/html",
        },
        progress: progress);

    Console.WriteLine(
        $"\nIngested {result.IngestedCount} pages, {result.FailedCount} failed, {result.SkippedCount} skipped.");

    if (evalMode)
    {
        var questionsPath = Path.Combine(AppContext.BaseDirectory, "eval", "vragen.json");
        var questions = await RetrievalEval.LoadAsync(questionsPath);
        var ingestedUrls = result.Ingested.Select(o => o.Id.Value).ToHashSet(StringComparer.Ordinal);
        await RetrievalEval.RunAsync(pipeline, questions, RetrievalEval.Variants(rerankerRegistered), ingestedUrls);
        if (!rerankerRegistered)
        {
            Console.WriteLine("\nReranker variants skipped: set COHERE_API_KEY to include them.");
        }

        return 0;
    }

    // --- Ask ---
    Console.WriteLine("\nStel een vraag over zorg en gezondheid (of 'quit'):\n");
    while (true)
    {
        Console.Write("> ");
        var question = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(question) || question.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        var sources = new List<string>();
        await foreach (var update in pipeline.AskStreamingAsync(question))
        {
            if (update.Sources is { Count: > 0 })
            {
                foreach (var source in update.Sources)
                {
                    var url = source.Chunk.DocumentId.Value;
                    if (!sources.Contains(url, StringComparer.Ordinal)) sources.Add(url);
                }
            }

            if (update.TextDelta is not null)
            {
                Console.Write(update.TextDelta);
            }
        }

        Console.WriteLine("\n\nBronnen:");
        foreach (var url in sources)
        {
            Console.WriteLine($"  - {url}");
        }

        Console.WriteLine();
    }

    return 0;
}
finally
{
    Console.WriteLine("Stopping PostgreSQL container...");
    await postgres.DisposeAsync();
}

/// <summary>
/// Yields the first <c>max</c> entries of another provider and stops it there. Because the sitemap
/// is walked lazily, stopping early also stops fetching further shards.
/// </summary>
internal sealed class FirstPages(IFileContentProvider inner, int max) : IFileContentProvider
{
    public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (max <= 0) yield break;

        var yielded = 0;
        await foreach (var entry in inner.GetFilesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
            // Checked after yielding, not before reading the next entry: reading it could fetch
            // another sitemap shard only to throw it away.
            if (++yielded == max) yield break;
        }
    }
}

/// <summary>
/// Yields each page id once across several providers, in order, and leaves out the site's FAQ
/// index pages.
/// </summary>
/// <remarks>
/// An FAQ index page (…/vraag-en-antwoord) lists question titles and links to the answers, with no
/// answer text. Indexed, it matches a citizen's question very well and gives the answer nothing to
/// work with. The crawler still fetches it to follow its links; it just is not ingested.
/// </remarks>
internal sealed class UniquePages(params IFileContentProvider[] providers) : IFileContentProvider
{
    public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            await foreach (var entry in provider.GetFilesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (entry.IsSuccess)
                {
                    var id = entry.Value.Id.Value;
                    if (id.EndsWith("/vraag-en-antwoord", StringComparison.Ordinal) || !seen.Add(id)) continue;
                }

                yield return entry;
            }
        }
    }
}
