using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The self-query cell: a real model writes the corpus filter, and the pipeline applies it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the tag-filtered cell with the filter written by a model instead of by hand</b>, over
/// the same SciFact+FiQA store. That pairing is what makes the figure readable: the hand-filtered
/// cell reproduces SciFact's standalone 0.67742 exactly, so any gap here is attributable, and to
/// one of exactly two causes — the model naming the wrong corpus, or the post-retrieval wiring
/// discarding hits it cannot replace.
/// </para>
/// <para>
/// <b>The second cause is structural and applies even when the model is right.</b> Self-query sets
/// <c>RetrievalOptions.Filter</c>, which <c>FilterBehavior</c> runs as <c>results.Where(...)</c>
/// AFTER the search — no over-fetch, no backfill. A perfect filter on a two-corpus store still
/// returns a short page. <c>DiscardedHitCount</c> measures that directly, so the two causes are
/// separated in the output rather than pooled into one disappointing number.
/// </para>
/// <para>
/// <b>Cost</b>: one model call per query, cached; the pilot measured the shape and the counting
/// pass priced it at about a cent for 300 queries. Retrieval runs twice per query — the second is
/// the unfiltered control that makes the discard visible — and costs no model call.
/// </para>
/// </remarks>
public sealed class BeirSelfQueryTests(ITestOutputHelper output)
{
    private const string OtherCorpus = "fiqa";
    private const string GenerateVariable = "RAGNET_SELF_QUERY_GENERATE";
    private const string ApiKeyVariable = "OPENROUTER_API_KEY";
    private const string CacheSubdirectory = "self-query";

    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1");

    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData("scifact")]
    public async Task NdcgAt10_UnderSelfQuery_MeasuresWithTheModelProvablyFiltering(string datasetName)
    {
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);

        Assert.SkipUnless(
            descriptor.Supports(BeirProtocol.RealSelfQuery),
            $"{datasetName} does not declare the RealSelfQuery protocol applicable, so measuring " +
            "it would produce a number that means nothing.");

        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);

        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.RealSelfQuery, out var budgetReason),
            budgetReason);

        var cache = new GraphExtractionCache(
            cacheDirectory,
            GraphExtractionModelIdentity.ModelName,
            SelfQueryGate.Mode(GenerateVariable, out var generating),
            CacheSubdirectory);

        Assert.SkipWhen(
            !generating && !SelfQueryGate.HasEntries(cache),
            $"{GenerateVariable} is unset and the {CacheSubdirectory} cache is empty, so there is " +
            "nothing to replay and nothing may be spent.");

        var ct = TestContext.Current.CancellationToken;

        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);
        var other = await BeirHarness.LoadAsync(
            BeirDatasetDescriptor.ByName(OtherCorpus), cacheDirectory, " ", ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);

        var combined = await BuildTwoCorpusUnitsAsync(dataset, other, datasetName, ct);

        using var chat = OpenClient(cache, generating);
        using var row = new SelfQueryAblationRow(chat, generator, SelfQuerySchema.Corpus, datasetName);

        var run = await BeirHarness.MeasureAsync(
            descriptor, dataset, combined, row, generator, embeddings, ct);

        _output.WriteLine(FormattableString.Invariant($"""
            === {descriptor.Name} · {row.Name} ===
            {row.QueryCount} queries: {row.FilteredQueryCount} came back with a changed page, {row.UnchangedPageCount} unchanged.
            {row.CorrectCorpusCount} kept at least one '{datasetName}' chunk; {row.DiscardedHitCount} hits discarded in total.
            cache: {cache.Hits} hits, {cache.Misses} misses (misses are what was paid for).
            {run.Describe()}
            Hand-filtered control for the same store: 0.67742 (RealTagFiltered, pre-filtered).
            """));

        // Before the figure is read as self-query's: the model has to have filtered something.
        row.AssertTheModelActuallyFiltered(descriptor.Name);

        BeirReproduction.AssertReproduces(
            datasetName, BeirProtocol.RealSelfQuery, run.NdcgAt10, _output);
    }

    /// <summary>Chunks both corpora and tags every unit with the corpus it came from.</summary>
    private static async Task<IReadOnlyList<TextChunk>> BuildTwoCorpusUnitsAsync(
        BeirDataset dataset, BeirDataset other, string datasetName, CancellationToken ct)
    {
        var own = await BeirRealChunkingTests.ChunkAsync(dataset.Documents, ct);
        var foreign = await BeirRealChunkingTests.ChunkAsync(other.Documents, ct);

        Tag(own, datasetName);
        Tag(foreign, OtherCorpus);

        Assert.True(
            own.Count > 0 && foreign.Count > 0,
            "one of the two corpora produced no units, so the store holds one corpus and the " +
            "filter has nothing to exclude -- the cell would measure unfiltered retrieval.");

        var combined = new List<TextChunk>(own.Count + foreign.Count);
        combined.AddRange(own);
        combined.AddRange(foreign);

        return combined;
    }

    private static void Tag(IReadOnlyList<TextChunk> units, string corpus)
    {
        for (var i = 0; i < units.Count; i++)
        {
            units[i].Metadata[TagFilteredAblationRow.TagKey] = corpus;
        }
    }

    private static CachedGraphRagClient OpenClient(GraphExtractionCache cache, bool generating)
    {
        if (!generating)
        {
            return new CachedGraphRagClient(
                cache, inner: null, GraphExtractionModelIdentity.ExtractionTemperature);
        }

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(apiKey),
            $"{GenerateVariable} is set but {ApiKeyVariable} is not; nothing can be generated.");

        var model = new OpenAIClient(
                new ApiKeyCredential(apiKey!),
                new OpenAIClientOptions { Endpoint = OpenRouterEndpoint })
            .GetChatClient(GraphExtractionModelIdentity.ModelName)
            .AsIChatClient();

        return new CachedGraphRagClient(
            cache, model, GraphExtractionModelIdentity.ExtractionTemperature);
    }
}
