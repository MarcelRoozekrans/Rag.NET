using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.GraphRag;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The mind-map cell: a real model turns each article of the sixty-document
/// <see cref="MultiHopRagSlice"/> into a titled tree, through the shipped
/// <see cref="MindMapExtractor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is an exercise, not a quality figure, and the entry asked for exactly that.</b> There is
/// no ranking metric here and no control arm: a mind map has no qrels. What it establishes is that
/// the shipped extractor, given real articles and a real model, produces real trees — which is the
/// question Milestone 6.0 asks of every Done row.
/// </para>
/// <para>
/// <b>THE GUARD IS THE POINT, because this extractor cannot fail loudly.</b>
/// <see cref="MindMapExtractor.ExtractAsync"/> returns <c>EmptyRoot()</c> — <c>("", "", [])</c> — on
/// an LLM failure AND on an unparseable reply, and its own summary says it "never throws". So a run
/// where every call failed returns sixty empty roots and reports as a completed extraction over
/// sixty documents. <see cref="AssertTheModelActuallyBuiltTrees"/> refuses that.
/// </para>
/// <para>
/// <b>That guard is not hypothetical.</b> On 2026-09-06 the deep-research cell reproduced its
/// control exactly, with zero model calls, because <c>DeepResearchRetriever</c> swallowed a cache
/// refusal as a sufficiency verdict — a clean null result describing a feature that never ran. This
/// extractor has the same fail-open shape. It differs in one lucky respect: it passes
/// <c>options: null</c>, so it never sets the <c>ResponseFormat</c> that
/// <c>CachedGraphRagClient</c> refused until that same day.
/// </para>
/// <para>
/// <b>Cost</b>: one call per article, 60 in total — about $0.03. The counting pass priced this
/// feature at $1.59 from a literal 3,000 documents, which was wrong twice over: MultiHop-RAG's
/// corpus is 609 documents and this cell runs the 60-document slice. Cached on disk; a re-run
/// replays free.
/// </para>
/// </remarks>
public sealed class BeirMindMapTests(ITestOutputHelper output)
{
    private const string GenerateVariable = "RAGNET_MIND_MAP_GENERATE";
    private const string ApiKeyVariable = "OPENROUTER_API_KEY";
    private const string CacheSubdirectory = "mind-map";

    /// <summary>
    /// Articles that must come back with a non-empty titled root carrying at least one child.
    /// </summary>
    /// <remarks>
    /// Not all sixty. The extractor is allowed to find a document unmappable, and a run that
    /// demanded perfection would fail on the model rather than on the mechanism. Two thirds is far
    /// enough above the failure mode this guards — a total failure returns <b>zero</b> — to
    /// distinguish "the extractor works" from "the extractor ran".
    /// </remarks>
    private const double MinimumTreeShare = 2.0 / 3.0;

    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1");

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task MindMap_OverTheMultiHopRagSlice_BuildsTreesFromRealArticles()
    {
        var descriptor = BeirDatasetDescriptor.ByName(MultiHopRagSlice.DatasetName);

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
            "nothing to replay and nothing may be spent.");

        var ct = TestContext.Current.CancellationToken;

        var dataset = await BeirHarness.LoadAsync(
            descriptor, cacheDirectory, BeirLoader.DefaultTitleTextSeparator, ct);
        var documents = MultiHopRagSlice.Documents(dataset.Documents);

        Assert.True(
            documents.Count == MultiHopRagSlice.TargetDocumentCount,
            $"the slice resolved {documents.Count} documents rather than " +
            $"{MultiHopRagSlice.TargetDocumentCount}; the cell's cost and its guard are both stated " +
            "against that count, so a short slice fails rather than measuring something else.");

        using var chat = OpenClient(cache, generating);
        var extractor = new MindMapExtractor(chat, graphStore: null, new MindMapOptions());

        var (trees, empties, nodes, deepest) = await ExtractAllAsync(extractor, documents, ct);

        _output.WriteLine(FormattableString.Invariant($"""
            === {descriptor.Name} · mind-map extraction over the {documents.Count}-article slice ===
            {trees} of {documents.Count} articles produced a titled root with children; {empties} came back empty.
            {nodes} nodes in total, deepest tree {deepest} levels.
            cache: {cache.Hits} hits, {cache.Misses} misses (misses are what was paid for).
            An empty root is what this extractor returns on BOTH an LLM failure and an unparseable
            reply, so the count above is the only thing separating a working run from a failed one.
            """));

        AssertTheModelActuallyBuiltTrees(trees, documents.Count);
    }

    /// <summary>
    /// Refuses a run whose trees are mostly empty, before it is read as an exercise of the feature.
    /// </summary>
    /// <param name="trees">Articles that produced a titled root with at least one child.</param>
    /// <param name="total">Articles attempted.</param>
    /// <remarks>
    /// <c>MindMapExtractor</c> returns an empty root on any failure and never throws, so a run where
    /// nothing reached the model completes, reports sixty documents processed, and is
    /// indistinguishable from success anywhere except here.
    /// </remarks>
    private static void AssertTheModelActuallyBuiltTrees(int trees, int total)
    {
        var required = (int)Math.Ceiling(total * MinimumTreeShare);

        Assert.True(
            trees >= required,
            FormattableString.Invariant(
                $"only {trees} of {total} articles produced a titled root with children, below the ") +
            FormattableString.Invariant($"{required} this cell requires. ") +
            "MindMapExtractor returns an empty root on an LLM failure and on an unparseable reply " +
            "alike, and never throws, so a run that reached no model at all looks exactly like " +
            "this. Check the cache's miss count before concluding the model is at fault.");
    }

    /// <summary>Extracts every article, counting what came back rather than only that it did.</summary>
    private static async Task<(int Trees, int Empties, int Nodes, int Deepest)> ExtractAllAsync(
        MindMapExtractor extractor,
        IReadOnlyList<BeirDocument> documents,
        CancellationToken ct)
    {
        var trees = 0;
        var empties = 0;
        var nodes = 0;
        var deepest = 0;

        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            var root = await extractor.ExtractAsync(document.Text, document.Id, ct);

            if (string.IsNullOrWhiteSpace(root.Title) && root.Children.Count == 0)
            {
                empties++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(root.Title) && root.Children.Count > 0)
                trees++;

            nodes += Count(root);
            deepest = Math.Max(deepest, Depth(root));
        }

        return (trees, empties, nodes, deepest);
    }

    private static int Count(MindMapNode node)
    {
        var total = 1;
        for (var i = 0; i < node.Children.Count; i++)
            total += Count(node.Children[i]);

        return total;
    }

    private static int Depth(MindMapNode node)
    {
        var deepest = 0;
        for (var i = 0; i < node.Children.Count; i++)
            deepest = Math.Max(deepest, Depth(node.Children[i]));

        return deepest + 1;
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
