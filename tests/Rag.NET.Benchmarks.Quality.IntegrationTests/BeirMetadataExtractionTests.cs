using System.ClientModel;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenAI;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// LLM metadata extraction over real corpora: the whole of SciFact (20,155 chunks) against a
/// capped FiQA control (1,000), one model call each.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the pilot established and this does not need to re-establish.</b> The 120-chunk pilot
/// showed the mechanism works and the schema constrains the model exactly — every extracted value
/// was one of the two the schema names, and every one matched the chunk's own corpus. What it also
/// showed is that extraction can return nothing: the model answered a literal <c>{}</c> for 24 of
/// 120 chunks, all of them FiQA. SciFact scored 60/60 there, so this run's job is to say whether
/// that holds at 336x the scale or whether the pilot's slice was flattering.
/// </para>
/// <para>
/// <b>Coverage is the figure, not accuracy.</b> Each corpus is one domain, so a correct answer is
/// always the same word and accuracy is nearly free; what varies is whether the model answers at all.
/// </para>
/// <para>
/// <b>FiQA is the control, and it is what makes this a measurement rather than a number.</b> The
/// claim worth making is that the pilot's one-sided misses are a property of the CORPUS and not of
/// <see cref="LlmMetadataExtractionBehavior"/>, and a single-corpus run cannot separate those two.
/// The arms differ in the corpus and in nothing else — same behaviour, same model, same schema,
/// same temperature, same cache.
/// <see cref="LlmMetadataExtractionBehavior"/> adds metadata with <c>TryAdd</c> and logs a per-chunk
/// warning on failure, so an unlabelled chunk is silent in every consumer downstream — a filter over
/// that key would quietly not match it.
/// </para>
/// <para>
/// <b>It runs in batches so a five-hour run is not silent</b>, and it is resumable by construction:
/// every reply is written to the cache as it arrives, so an interrupted run replays what it already
/// paid for and spends only on the remainder. That matters more than usual at 20,155 sequential
/// calls — the pilot measured about a second each.
/// </para>
/// </remarks>
public sealed class BeirMetadataExtractionTests(ITestOutputHelper output)
{
    private const string GenerateVariable = "RAGNET_METADATA_EXTRACTION_GENERATE";
    private const string ApiKeyVariable = "OPENROUTER_API_KEY";
    private const string CacheSubdirectory = "metadata-extraction";
    private const string DomainKey = "domain";
    private const int BatchSize = 1_000;

    /// <summary>Percentage points either side of a pinned coverage figure, the same ±0.5% shape the nDCG cells use at ±0.005.</summary>
    private const double CoverageBand = 0.5;

    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1");

    private static readonly IReadOnlyList<AttributeInfo> DomainSchema =
    [
        new(
            DomainKey,
            "The subject domain of this text. Answer with exactly one word: 'biomedical' for " +
            "clinical, biological or medical research writing, or 'finance' for personal finance, " +
            "investing, tax or banking discussion."),
    ];

    private readonly ITestOutputHelper _output = output;

    /// <param name="datasetName">The BEIR corpus to extract over.</param>
    /// <param name="expected">
    /// The one value the schema should produce for every chunk of this corpus. The corpus IS the
    /// ground truth — that is the whole reason the schema asks for domain rather than for "topic"
    /// or "keywords", which would produce values nothing could check.
    /// </param>
    /// <param name="unitCap">
    /// <c>0</c> runs the whole corpus. A positive value takes the first <c>n</c> units in corpus
    /// order — no sampling, no shuffle, so the arm is reproducible and the pilot's units are a
    /// strict prefix of it. FiQA is capped because its 121,236 units are 6x SciFact's, which is
    /// ~$28 and ~44 hours: out on cost. What it is here for is the CONTROL, and a control needs a
    /// defensible n rather than the whole corpus.
    /// </param>
    /// <param name="pinnedCoverage">
    /// Percent of chunks that came back carrying the key, measured 2026-09-05 and asserted within
    /// <see cref="CoverageBand"/>. <b>Replay is deterministic, so this pins the SHIPPED PATH rather
    /// than the model</b>: the cached replies do not move, so a coverage change means
    /// <see cref="LlmMetadataExtractionBehavior"/> stopped attaching what it used to — the silent
    /// failure this cell exists to make loud. Regenerating against a different model is expected to
    /// move it, and is a re-measurement rather than a regression.
    /// </param>
    [Theory]
    [InlineData("scifact", "biomedical", 0, 98.79)]
    [InlineData("fiqa", "finance", 1_000, 62.70)]
    public async Task MetadataExtraction_OverARealCorpus_ReportsCoverageAndAccuracy(
        string datasetName,
        string expected,
        int unitCap,
        double pinnedCoverage)
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out _, out _, out var cacheDirectory),
            BeirHarness.SkipReason);

        Assert.SkipUnless(
            BeirRunBudget.IsOptedInFor(
                Environment.GetEnvironmentVariable(BeirRunBudget.OptInVariable), datasetName),
            $"{BeirRunBudget.OptInVariable} does not name {datasetName}. The SciFact arm makes " +
            "20,155 model calls, about five hours and $4.63 at the rate the pilot measured; the " +
            "capped FiQA arm makes 1,000. Both are opt-in like every other long run here.");

        var cache = new GraphExtractionCache(
            cacheDirectory,
            GraphExtractionModelIdentity.ModelName,
            Mode(out var generating),
            CacheSubdirectory);

        Assert.SkipWhen(
            !generating && !HasEntries(cache),
            $"{GenerateVariable} is unset and the {CacheSubdirectory} cache is empty.");

        var ct = TestContext.Current.CancellationToken;

        var units = await LoadUnitsAsync(datasetName, cacheDirectory, unitCap, ct);

        _output.WriteLine(FormattableString.Invariant(
            $"{units.Count} units to extract over, in batches of {BatchSize}."));

        var (extracted, correct, other, elapsed) =
            await RunBatchesAsync(cache, generating, units, expected);

        Report(datasetName, expected, units.Count, extracted, correct, other, elapsed, cache);

        Assert.True(
            extracted > 0,
            $"{datasetName}: not one of {units.Count} chunks came back with a '{DomainKey}' value.");

        Assert.True(
            correct > 0,
            FormattableString.Invariant(
                $"{datasetName}: {extracted} chunks carried a value and none was '{expected}'. ") +
            "Every one describes something other than the corpus it came from.");

        var coverage = 100.0 * extracted / units.Count;
        Assert.True(
            Math.Abs(coverage - pinnedCoverage) <= CoverageBand,
            FormattableString.Invariant(
                $"{datasetName}: coverage {coverage:F2}% against the pinned {pinnedCoverage:F2}%, ") +
            FormattableString.Invariant($"outside the ±{CoverageBand} band. ") +
            "On a cached replay the replies do not move, so this is the shipped attachment path " +
            "changing what it writes -- exactly the silent shortfall this cell exists to catch. " +
            "If the cache was regenerated against a different model, re-pin rather than widen.");
    }



    /// <summary>
    /// Loads a corpus and returns the units to extract over, in corpus order and capped.
    /// </summary>
    /// <remarks>
    /// Chunking the whole of FiQA to keep 1,000 units would cost minutes for nothing, so a capped
    /// arm chunks only <paramref name="unitCap"/> documents — every corpus here averages above one
    /// unit per document, and the assert refuses to run a short arm rather than reporting a
    /// coverage percentage over a denominator nobody recorded.
    /// </remarks>
    private static async Task<IReadOnlyList<TextChunk>> LoadUnitsAsync(
        string datasetName,
        string cacheDirectory,
        int unitCap,
        CancellationToken ct)
    {
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);

        var documents = unitCap == 0
            ? dataset.Documents
            : dataset.Documents.Take(unitCap).ToList();

        var chunked = await BeirRealChunkingTests.ChunkAsync(documents, ct);
        var units = unitCap == 0 ? chunked : chunked.Take(unitCap).ToList();

        Assert.True(
            unitCap == 0 || units.Count == unitCap,
            $"{datasetName}: asked for {unitCap} units and the first {documents.Count} documents " +
            $"yielded {chunked.Count}. A short arm would report a coverage percentage over a " +
            "different denominator than the one recorded, so it fails rather than runs.");

        return units;
    }

    /// <summary>Prints the run's figures, coverage first because it is the one that varies.</summary>
    private void Report(
        string datasetName,
        string expected,
        int total,
        int extracted,
        int correct,
        Dictionary<string, int> other,
        TimeSpan elapsed,
        GraphExtractionCache cache)
    {
        var others = other.Count == 0
            ? "none"
            : string.Join(
                ", ",
                other.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key}x{p.Value}"));

        _output.WriteLine(FormattableString.Invariant($"""
            === {datasetName} · llm metadata extraction over a real corpus ===
            {total} chunks, {extracted} carried a '{DomainKey}' value ({100.0 * extracted / total:F2}% coverage).
            {correct} of {extracted} matched '{expected}' ({(extracted == 0 ? 0 : 100.0 * correct / extracted):F2}% of those extracted).
            values other than '{expected}': {others}
            cache: {cache.Hits} hits, {cache.Misses} misses (misses are what was paid for).
            elapsed {elapsed.TotalMinutes:F1} min
            Pilot for comparison: SciFact 60/60 extracted, FiQA 36/60.
            """));
    }

    /// <summary>Runs every batch, printing progress so a five-hour run is not silent.</summary>
    private async Task<(int Extracted, int Correct, Dictionary<string, int> Other, TimeSpan Elapsed)>
        RunBatchesAsync(
            GraphExtractionCache cache,
            bool generating,
            IReadOnlyList<TextChunk> units,
            string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var stopwatch = Stopwatch.StartNew();
        var extracted = 0;
        var correct = 0;
        var other = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using var client = OpenClient(cache, generating);

        for (var start = 0; start < units.Count; start += BatchSize)
        {
            var take = Math.Min(BatchSize, units.Count - start);
            var batch = new List<TextChunk>(take);
            for (var i = 0; i < take; i++)
            {
                batch.Add(units[start + i]);
            }

            var (batchExtracted, batchCorrect) =
                await ExtractBatchAsync(client, batch, expected, other, ct);

            extracted += batchExtracted;
            correct += batchCorrect;

            _output.WriteLine(FormattableString.Invariant(
                $"  {start + batch.Count}/{units.Count}  extracted {extracted}  correct {correct}  ")
                + FormattableString.Invariant(
                    $"cache {cache.Hits}h/{cache.Misses}m  {stopwatch.Elapsed.TotalMinutes:F1} min"));
        }

        stopwatch.Stop();
        return (extracted, correct, other, stopwatch.Elapsed);
    }

    /// <summary>Runs one batch through the real ingest behaviour and scores what it wrote.</summary>
    private static async Task<(int Extracted, int Correct)> ExtractBatchAsync(
        IChatClient client,
        IReadOnlyList<TextChunk> chunks,
        string expected,
        Dictionary<string, int> other,
        CancellationToken ct)
    {
        var behavior = new LlmMetadataExtractionBehavior
        {
            ChatClient = client,
            ExtractionOptions = new LlmMetadataExtractionOptions { Schema = DomainSchema },
        };

        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("extraction-run"),
            FileName = "extraction-run.txt",
        };

        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = metadata,
        };

        foreach (var chunk in chunks)
        {
            ctx.Chunks.Add(chunk);
        }

        await behavior.HandleAsync(
            ctx, ct,
            static (c, _) => ValueTask.FromResult(
                new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        var extracted = 0;
        var correct = 0;

        foreach (var chunk in ctx.Chunks)
        {
            if (!chunk.Metadata.TryGetValue(DomainKey, out var value))
                continue;

            extracted++;
            var text = value.StringValue ?? string.Empty;

            if (text.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                correct++;
            }
            else
            {
                other[text] = other.GetValueOrDefault(text) + 1;
            }
        }

        return (extracted, correct);
    }

    // NOTE: duplicated from BeirMetadataExtractionPilotTests, and both are duplicated from
    // SelfQueryGate now that #469 has landed. Collapse all three into SelfQueryGate (renamed for
    // what it actually is -- a cache gate, not a self-query one) once this run is recorded.
    private static GraphExtractionCacheMode Mode(out bool generating)
    {
        var flag = Environment.GetEnvironmentVariable(GenerateVariable);
        generating = !string.IsNullOrWhiteSpace(flag)
            && !string.Equals(flag, "0", StringComparison.Ordinal)
            && !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase);

        return generating ? GraphExtractionCacheMode.Fill : GraphExtractionCacheMode.RefuseOnMiss;
    }

    private static bool HasEntries(GraphExtractionCache cache) =>
        Directory.Exists(cache.EntryDirectory)
        && Directory.EnumerateFiles(cache.EntryDirectory, "*", SearchOption.AllDirectories).Any();

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
