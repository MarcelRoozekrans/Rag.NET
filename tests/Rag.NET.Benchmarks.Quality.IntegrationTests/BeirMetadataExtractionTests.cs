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
/// LLM metadata extraction over the whole SciFact corpus: 20,155 chunks, one model call each.
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
/// <b>Coverage is the figure, not accuracy.</b> SciFact is one domain, so a correct answer is always
/// the same word and accuracy is nearly free; what varies is whether the model answers at all.
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
    private const string Expected = "biomedical";
    private const int BatchSize = 1_000;

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

    [Theory]
    [InlineData("scifact")]
    public async Task MetadataExtraction_OverTheWholeCorpus_ReportsCoverageAndAccuracy(
        string datasetName)
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out _, out _, out var cacheDirectory),
            BeirHarness.SkipReason);

        Assert.SkipUnless(
            BeirRunBudget.IsOptedInFor(
                Environment.GetEnvironmentVariable(BeirRunBudget.OptInVariable), datasetName),
            $"{BeirRunBudget.OptInVariable} does not name {datasetName}. This cell makes 20,155 " +
            "model calls, about five hours and $4.63 at the rate the pilot measured, so it is " +
            "opt-in like every other long run here.");

        var cache = new GraphExtractionCache(
            cacheDirectory,
            GraphExtractionModelIdentity.ModelName,
            Mode(out var generating),
            CacheSubdirectory);

        Assert.SkipWhen(
            !generating && !HasEntries(cache),
            $"{GenerateVariable} is unset and the {CacheSubdirectory} cache is empty.");

        var ct = TestContext.Current.CancellationToken;

        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);
        var units = await BeirRealChunkingTests.ChunkAsync(dataset.Documents, ct);

        _output.WriteLine(FormattableString.Invariant(
            $"{units.Count} units to extract over, in batches of {BatchSize}."));

        var (extracted, correct, other, elapsed) = await RunBatchesAsync(cache, generating, units, datasetName);

        Report(datasetName, units.Count, extracted, correct, other, elapsed, cache);

        Assert.True(
            extracted > 0,
            $"{datasetName}: not one of {units.Count} chunks came back with a '{DomainKey}' value.");

        Assert.True(
            correct > 0,
            FormattableString.Invariant(
                $"{datasetName}: {extracted} chunks carried a value and none was '{Expected}'. ") +
            "Every one describes something other than the corpus it came from.");
    }



    /// <summary>Prints the run's figures, coverage first because it is the one that varies.</summary>
    private void Report(
        string datasetName,
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
            === {datasetName} · llm metadata extraction over the whole corpus ===
            {total} chunks, {extracted} carried a '{DomainKey}' value ({100.0 * extracted / total:F2}% coverage).
            {correct} of {extracted} matched '{Expected}' ({(extracted == 0 ? 0 : 100.0 * correct / extracted):F2}% of those extracted).
            values other than '{Expected}': {others}
            cache: {cache.Hits} hits, {cache.Misses} misses (misses are what was paid for).
            elapsed {elapsed.TotalMinutes:F1} min
            Pilot for comparison: SciFact 60/60 extracted, 60/60 correct.
            """));
    }

    /// <summary>Runs every batch, printing progress so a five-hour run is not silent.</summary>
    private async Task<(int Extracted, int Correct, Dictionary<string, int> Other, TimeSpan Elapsed)>
        RunBatchesAsync(
            GraphExtractionCache cache,
            bool generating,
            IReadOnlyList<TextChunk> units,
            string datasetName)
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

            var (batchExtracted, batchCorrect) = await ExtractBatchAsync(client, batch, other, ct);

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
            GetNextBm25DocId = () => 0,
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

            if (text.Contains(Expected, StringComparison.OrdinalIgnoreCase))
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
