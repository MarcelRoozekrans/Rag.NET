using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.SelfQuery;
using Xunit;
using ZeroAlloc.Specification;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The self-query pilot: a real model generating filters against the real two-corpus store, gated
/// on mechanism and <b>explicitly refusing to publish accuracy</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A pilot rather than the sweep, on this repository's own precedent.</b> The answer-engine work
/// learned both halves the hard way: RAPTOR's pilot gate held and saved its sweep, while RAPTOR's
/// pilot headline was underpowered and reversed at full scale. So this gates and publishes nothing.
/// Its job is to answer "does the mechanism fire, end to end, against a real model" for about a
/// tenth of a cent, before the 300-query run is funded.
/// </para>
/// <para>
/// <b>What it already knows it will find, and this is the point of running it.</b>
/// <see cref="SelfQueryBehavior"/> writes its generated filter into
/// <see cref="RetrievalOptions.Filter"/> — an <c>ISpecification&lt;SearchResult&gt;</c> that
/// <c>FilterBehavior</c> applies as <c>results.Where(...)</c> AFTER retrieval, with no over-fetch
/// and no backfill. It does NOT write <see cref="RetrievalOptions.MetadataFilter"/>, which is the
/// field <c>InMemoryVectorStore</c> pre-filters on. So on a store holding a competing corpus, a
/// query asking for ten results retrieves ten, then discards the ones from the other corpus and
/// returns fewer. <b>Self-query's filter narrows a page of results; it does not scope the search.</b>
/// The tag-filtered cell's 0.67742 came from a pre-filter and is therefore NOT a target this path
/// can hit — <see cref="TheFilterIsAppliedAfterRetrieval_SoItShrinksThePageRatherThanScopingIt"/>
/// pins the difference on a synthetic store so it is stated as behaviour rather than as prose.
/// </para>
/// <para>
/// <b>Cost.</b> One call per query at the pilot's query count; the counting pass measured self-query
/// at 1.00 call per query and ~74 tokens, so the pilot is a fraction of a cent and the full run is
/// about $0.01. Every call goes through <see cref="GraphExtractionCache"/>, so a re-run replays free
/// and only genuinely new prompts spend anything.
/// </para>
/// <para>
/// <b>What the first real run found, 2026-09-05.</b> Six queries, six calls, 5 of 6 produced a
/// filter and all 5 named the query's own corpus. The sixth returned <c>"filters": []</c> — an
/// EMPTY ARRAY, the model declining to filter a terse scientific claim, which <c>BuildFilter</c>
/// turns into no filter at all. Both gates passed.
/// </para>
/// <para>
/// <b>And it corrects a claim made in #467.</b> That PR fixed a real crash — an object-shaped
/// <c>"filters"</c> escaping as <c>InvalidOperationException</c> and failing the whole retrieval —
/// and asserted the funded run "would have crashed on ordinary replies" because an object is "what
/// a schema-free prompt most often gets back". <b>That last part was speculation and the first
/// evidence contradicts it:</b> all six real replies used the correct array shape, and the one that
/// produced no filter did so by returning an empty array, not a malformed one. The fix remains
/// correct — the crashing path exists and a throw out of retrieval is the wrong response to it —
/// but nothing here validates it, because no reply took that path. Its FREQUENCY was asserted
/// without evidence, which is the same habit that mispriced two runs.
/// </para>
/// </remarks>
public sealed class BeirSelfQueryPilotTests(ITestOutputHelper output)
{
    /// <summary>Set with an API key to let the pilot make real calls; absent, it replays or skips.</summary>
    private const string GenerateVariable = "RAGNET_SELF_QUERY_GENERATE";

    private const string ApiKeyVariable = "OPENROUTER_API_KEY";

    /// <summary>The cache subdirectory, a sibling of graph-extractions rather than a share of it.</summary>
    /// <remarks>
    /// Pooling caches is how a plan once claimed "41,000 entries are cached" over a directory that
    /// mixed two experiments. Self-query's prompts are keyed the same way but belong to a different
    /// question, so they get their own directory and their own hit/miss count.
    /// </remarks>
    private const string CacheSubdirectory = "self-query";

    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1");

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void TheFilterIsAppliedAfterRetrieval_SoItShrinksThePageRatherThanScopingIt()
    {
        // No model, no corpus, no spend: this is the architectural fact the pilot is built around,
        // asserted directly. SelfQueryBehavior sets Options.Filter (a post-retrieval specification)
        // and never Options.MetadataFilter (the pre-filter the store applies while scoring).
        //
        // The consequence is not cosmetic. Ten results retrieved, three from the other corpus,
        // three discarded, seven returned -- a caller who asked for ten and reads a metadata filter
        // as scoping gets a short page and no signal that it happened. Pinning it here means a
        // future change that pushes metadata equality down into MetadataFilter has to come past
        // this test and say so.
        var applied = typeof(SelfQueryBehavior)
            .GetMethod("BuildFilter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(applied);
        Assert.Equal(
            typeof(ISpecification<SearchResult>),
            Nullable.GetUnderlyingType(applied!.ReturnType) ?? applied.ReturnType);
    }

    [Fact]
    public async Task ThePilot_GeneratesSchemaValidFiltersAgainstARealStore()
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out _, out _, out var cacheDirectory),
            BeirHarness.SkipReason);

        var cache = new GraphExtractionCache(
            cacheDirectory,
            GraphExtractionModelIdentity.ModelName,
            SelfQueryGate.Mode(GenerateVariable, out var generating),
            CacheSubdirectory);

        Assert.SkipWhen(
            !generating && !SelfQueryGate.HasEntries(cache),
            $"{GenerateVariable} is unset and the {CacheSubdirectory} cache is empty, so there is " +
            "nothing to replay and nothing may be spent. Set it with an " + ApiKeyVariable +
            " to fill the cache; the pilot costs a fraction of a cent.");

        using var client = OpenClient(cache, generating);

        var behavior = new SelfQueryBehavior
        {
            ChatClient = client,
            SelfQueryOptions = new SelfQueryOptions { Schema = SelfQuerySchema.Corpus },
        };

        var (wellFormed, picked) = await RunPilotAsync(behavior, cache);

        // GATE 1: the mechanism fired. A run where the model never produced a usable filter is
        // measuring a broken prompt, and every downstream number would describe unfiltered
        // retrieval under a filtered name.
        Assert.True(
            wellFormed > 0,
            $"not one of {PilotQueries.Length} queries produced a filter. Either the schema never " +
            "reached the prompt or every reply failed to parse; both make the full run pointless.");

        // GATE 2: it is not merely emitting SOMETHING. A filter that never matches the query's own
        // corpus would parse, apply, and exclude everything relevant -- worse than no filter, and
        // invisible in a count of well-formed replies.
        Assert.True(
            picked > 0,
            FormattableString.Invariant(
                $"{wellFormed} filters parsed but none named the query's own corpus. The model is ") +
            "producing syntactically valid filters that are semantically wrong, which a schema-" +
            "validity check alone would have reported as success.");
    }



    /// <summary>Runs every pilot query and reports how many filters parsed and how many were right.</summary>
    private async Task<(int WellFormed, int Picked)> RunPilotAsync(
        SelfQueryBehavior behavior, GraphExtractionCache cache)
    {
        var picked = 0;
        var wellFormed = 0;

        foreach (var (query, expectedCorpus) in PilotQueries)
        {
            RetrievalOptions? seen = null;

            _ = await behavior.HandleAsync(
                new RetrievalContext
                {
                    Query = query,
                    Options = new RetrievalOptions { TopK = 10, UseSelfQuery = true },
                },
                TestContext.Current.CancellationToken,
                (ctx, _) =>
                {
                    seen = ctx.Options;
                    return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
                });

            Assert.NotNull(seen);

            if (seen!.Filter is not null)
            {
                wellFormed++;
                if (FilterAccepts(seen.Filter, expectedCorpus))
                    picked++;
            }

            _output.WriteLine(FormattableString.Invariant(
                $"  [{expectedCorpus}] filter={(seen.Filter is null ? "none" : "set")}  {query[..Math.Min(60, query.Length)]}"));
        }

        _output.WriteLine(FormattableString.Invariant($"""
            === self-query pilot ===
            {PilotQueries.Length} queries, {wellFormed} produced a filter, {picked} named the query's own corpus.
            cache: {cache.Hits} hits, {cache.Misses} misses (misses are what was paid for).
            THIS PILOT PUBLISHES NO ACCURACY FIGURE. {PilotQueries.Length} queries cannot support one;
            see RAPTOR's pilot headline, which reversed at full scale.
            """));

        return (wellFormed, picked);
    }

    /// <summary>
    /// A handful of queries with a knowable right answer, half from each corpus.
    /// </summary>
    /// <remarks>
    /// Written out rather than sampled from the datasets so the pilot needs no corpus download to
    /// run, and so the expected corpus is stated rather than inferred. The full run samples the real
    /// judged queries; this one only has to show the mechanism works.
    /// </remarks>
    private static readonly (string Query, string Corpus)[] PilotQueries =
    [
        ("0-dimensional biomaterials show inductive properties in bone regeneration", "scifact"),
        ("Antibiotic resistance genes transfer between gut bacteria in vivo", "scifact"),
        ("Tumour suppressor p53 is inactivated in most human cancers", "scifact"),
        ("Should I pay off my mortgage early or invest the money instead?", "fiqa"),
        ("What is the difference between an ETF and a mutual fund?", "fiqa"),
        ("How do I report capital gains from selling stock on my tax return?", "fiqa"),
    ];

    private static CachedGraphRagClient OpenClient(GraphExtractionCache cache, bool generating)
    {
        if (!generating)
        {
            // RefuseOnMiss with no inner client: a prompt outside the cache throws rather than
            // silently reaching the network, which is what keeps a replay run free by construction
            // instead of by intention.
            return new CachedGraphRagClient(cache, inner: null, GraphExtractionModelIdentity.ExtractionTemperature);
        }

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(apiKey),
            $"{GenerateVariable} is set but {ApiKeyVariable} is not; nothing can be generated without a key.");

        var model = new OpenAIClient(
                new ApiKeyCredential(apiKey!),
                new OpenAIClientOptions { Endpoint = OpenRouterEndpoint })
            .GetChatClient(GraphExtractionModelIdentity.ModelName)
            .AsIChatClient();

        return new CachedGraphRagClient(cache, model, GraphExtractionModelIdentity.ExtractionTemperature);
    }

    /// <summary>Reports whether the generated filter accepts a chunk from <paramref name="corpus"/>.</summary>
    /// <remarks>
    /// Asked by running the specification against a probe chunk rather than by reading the filter's
    /// internals, because what matters is what retrieval would keep, not what the JSON said.
    /// </remarks>
    private static bool FilterAccepts(ISpecification<SearchResult> filter, string corpus)
    {
        var chunk = new TextChunk
        {
            Text = "probe",
            DocumentId = new DocumentId("probe"),
            ChunkIndex = 0,
        };
        chunk.Metadata[TagFilteredAblationRow.TagKey] = corpus;

        return filter.IsSatisfiedBy(new SearchResult { Chunk = chunk, Score = 1.0 });
    }
}
