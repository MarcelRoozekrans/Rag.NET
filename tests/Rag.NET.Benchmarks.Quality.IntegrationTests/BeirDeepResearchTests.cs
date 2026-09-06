using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The deep-research cell: a real model judges whether the retrieved context answers the query and,
/// when it says no, writes sub-queries whose pages are folded into the result.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single corpus, unlike the two cells beside it.</b> Self-query and tag-filtering both need a
/// second corpus because they SCOPE retrieval and a one-corpus store gives them nothing to exclude.
/// Deep research scopes nothing — it adds — so a second corpus would multiply the cost and answer
/// no question. Its control is the Real dense figure on the same corpus.
/// </para>
/// <para>
/// <b>Two properties of the shipped feature travel with this figure and cannot be separated from
/// it.</b> First, the page is <b>not capped to <c>TopK</c></b>: the union of the inner page and
/// every sub-query's page is deduplicated and returned whole, so it is larger than the control's —
/// issue #475, characterised in <c>DeepResearchRetrieverTests</c>. Second, that union is sorted by
/// score, and a sub-query's scores come from a different query vector than the caller's, so a
/// chunk scoring well against a sub-query can outrank one scoring well against the question asked.
/// Measuring it as it ships was a deliberate choice: fixing the contract on the way to a benchmark
/// would publish a figure for code no released version has.
/// </para>
/// <para>
/// <b>THE CELL CAN SILENTLY MEASURE NOTHING, which is what the guard is for.</b>
/// <c>DeepResearchRetriever</c> fails open — an unreadable sufficiency reply is treated as
/// "sufficient", logged at warning, and the loop stops. A run where every reply failed to parse
/// returns the inner page on every query and reproduces the Real dense figure <b>exactly</b>: a
/// clean null result describing a feature that never ran. A benchmark reads no logs, so
/// <see cref="DeepResearchAblationRow.AssertTheLoopActuallyRan"/> fails the run instead. This is
/// the same shape as the SPLADE cell's expansion guard and the self-query cell's filter guard, and
/// the reason all three exist is that this phase has twice had to retract a figure whose mechanism
/// had not fired.
/// </para>
/// <para>
/// <b>Cost</b>: up to <c>MaxDepth</c> model calls per query — 3 at the shipped defaults, measured
/// rather than assumed by <c>LlmCallShapeTests.DeepResearch_MakesOneCallPerDepth</c> — so 900 calls
/// and about $0.89 for SciFact's 300 judged queries. Cached on disk; a re-run replays free.
/// Retrieval runs twice per query, once through each pipeline, and the control costs no model call.
/// </para>
/// </remarks>
public sealed class BeirDeepResearchTests(ITestOutputHelper output)
{
    private const string GenerateVariable = "RAGNET_DEEP_RESEARCH_GENERATE";
    private const string ApiKeyVariable = "OPENROUTER_API_KEY";
    private const string CacheSubdirectory = "deep-research";

    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1");

    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData("scifact")]
    public async Task NdcgAt10_UnderDeepResearch_MeasuresWithTheLoopProvablyRunning(string datasetName)
    {
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);

        Assert.SkipUnless(
            descriptor.Supports(BeirProtocol.RealDeepResearch),
            $"{datasetName} does not declare the RealDeepResearch protocol applicable, so " +
            "measuring it would produce a number that means nothing.");

        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);

        Assert.SkipWhen(
            BeirRunBudget.IsGatedOff(datasetName, BeirProtocol.RealDeepResearch, out var budgetReason),
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
        var units = await BeirRealChunkingTests.ChunkAsync(dataset.Documents, ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);

        var options = new DeepResearchOptions();

        using var chat = OpenClient(cache, generating);
        using var row = new DeepResearchAblationRow(chat, generator, options);

        var run = await BeirHarness.MeasureAsync(
            descriptor, dataset, units, row, generator, embeddings, ct);

        _output.WriteLine(FormattableString.Invariant($"""
            === {descriptor.Name} · {row.Name} ===
            MaxDepth {options.MaxDepth}, SubQueryCount {options.SubQueryCount}.
            {row.QueryCount} queries: {row.ExpandedQueryCount} expanded past the control's page, {row.QueryCount - row.ExpandedQueryCount} did not.
            {row.AddedChunkCount} chunks added in total; largest page returned {row.LargestPage} against a TopK of {row.RequestedTopK} (issue #475).
            cache: {cache.Hits} hits, {cache.Misses} misses (misses are what was paid for).
            {run.Describe()}
            Its control is the Real dense cell on this corpus, NOT a deep-research parity sibling.
            """));

        // Before the figure is read as deep research's: the loop has to have run.
        row.AssertTheLoopActuallyRan(descriptor.Name);

        BeirReproduction.AssertReproduces(
            datasetName, BeirProtocol.RealDeepResearch, run.NdcgAt10, _output);
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
