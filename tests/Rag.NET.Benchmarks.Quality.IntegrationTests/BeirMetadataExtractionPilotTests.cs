using System.ClientModel;
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
/// The metadata-extraction pilot: a real model tagging real chunks, gated on mechanism and on the
/// extracted values being <b>right</b> rather than merely well-formed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pilot at all, when the counting pass already priced this.</b> The counting pass measured
/// one call per chunk, which prices the full run at about $4.63 over SciFact's 20,155 units. It says
/// nothing about whether the extracted VALUES are usable. A run that returns 20,155 well-formed and
/// useless metadata dictionaries costs the same and looks identical in every figure — so this spends
/// about a twentieth of that to look at what actually comes back first.
/// </para>
/// <para>
/// <b>The schema has a knowable right answer, which is the whole design.</b> Asking a model to
/// extract "topic" or "keywords" produces values nothing can check, and the cell would report that
/// extraction "worked" on the strength of the JSON parsing. Instead the schema asks for the DOMAIN,
/// over chunks drawn from two corpora whose domain is known — SciFact is biomedical, FiQA is
/// personal finance. The source corpus is the ground truth, so accuracy is measurable rather than
/// asserted, exactly as the tag-filtered and self-query cells arranged.
/// </para>
/// <para>
/// <b>It publishes no accuracy figure.</b> Same reason as the self-query pilot: a pilot's headline
/// is underpowered, and RAPTOR's reversed at full scale. The gates below are pass/fail on mechanism;
/// the number belongs to the funded run.
/// </para>
/// <para>
/// <b>What the first run found, 2026-09-05: 120 chunks, 120 calls, and a coverage gap.</b> Every
/// extracted value was right — 96 of 96 matched the chunk's own corpus, and the only two values the
/// model ever emitted were <c>biomedical</c> and <c>finance</c>, so the schema constrained it
/// exactly. <b>But only 96 of 120 chunks got a value at all</b>, and the shortfall is entirely on
/// one side: SciFact 60/60, FiQA 36/60.
/// </para>
/// <para>
/// <b>The 24 misses are the model returning a literal <c>{}</c></b> — checked in the cache rather
/// than inferred. Nothing failed to parse and nothing threw; the model simply declined to classify
/// short finance forum posts. That is not a defect in <see cref="LlmMetadataExtractionBehavior"/>,
/// but it is a property worth knowing before funding 20,155 calls: extraction adds metadata with
/// <c>TryAdd</c> and logs a per-chunk warning, so a corpus can come out with 40% of its chunks
/// unlabelled and nothing louder than the log to say so.
/// </para>
/// </remarks>
public sealed class BeirMetadataExtractionPilotTests(ITestOutputHelper output)
{
    private const string GenerateVariable = "RAGNET_METADATA_EXTRACTION_GENERATE";
    private const string ApiKeyVariable = "OPENROUTER_API_KEY";
    private const string CacheSubdirectory = "metadata-extraction";

    /// <summary>The attribute whose value can be checked against the chunk's own corpus.</summary>
    private const string DomainKey = "domain";

    /// <summary>Chunks taken from each corpus. 60 is ~$0.01 and enough to see a pattern.</summary>
    private const int ChunksPerCorpus = 60;

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

    [Fact]
    public async Task ThePilot_ExtractsValuesThatMatchTheChunksOwnCorpus()
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out _, out _, out var cacheDirectory),
            BeirHarness.SkipReason);

        var cache = new GraphExtractionCache(
            cacheDirectory,
            GraphExtractionModelIdentity.ModelName,
            Mode(out var generating),
            CacheSubdirectory);

        Assert.SkipWhen(
            !generating && !HasEntries(cache),
            $"{GenerateVariable} is unset and the {CacheSubdirectory} cache is empty, so there is " +
            $"nothing to replay and nothing may be spent. Set it with an {ApiKeyVariable}; the " +
            "pilot is about a cent.");

        var ct = TestContext.Current.CancellationToken;

        var scifact = await Slice("scifact", cacheDirectory, ct);
        var fiqa = await Slice("fiqa", cacheDirectory, ct);

        using var client = OpenClient(cache, generating);

        var scored = await ExtractAsync(client, scifact, "biomedical", ct);
        var financeScored = await ExtractAsync(client, fiqa, "finance", ct);

        var extracted = scored.Extracted + financeScored.Extracted;
        var correct = scored.Correct + financeScored.Correct;
        var total = scifact.Count + fiqa.Count;

        _output.WriteLine(FormattableString.Invariant($"""
            === metadata-extraction pilot ===
            {total} chunks ({scifact.Count} biomedical, {fiqa.Count} finance).
            {extracted} carried a '{DomainKey}' value after extraction; {correct} matched the chunk's own corpus.
            biomedical: {scored.Correct}/{scored.Extracted} extracted correct. finance: {financeScored.Correct}/{financeScored.Extracted}.
            cache: {cache.Hits} hits, {cache.Misses} misses (misses are what was paid for).
            values seen: {string.Join(", ", scored.Values.Concat(financeScored.Values).Distinct(StringComparer.OrdinalIgnoreCase).Take(12))}
            THIS PILOT PUBLISHES NO ACCURACY FIGURE. {total} chunks cannot support one.
            """));

        // GATE 1: the mechanism fired. Extraction that produced no metadata at all means a broken
        // prompt or a schema that never reached it, and the funded run would spend $4.63 to learn
        // the same thing 20,155 times.
        Assert.True(
            extracted > 0,
            FormattableString.Invariant(
                $"not one of {total} chunks came back with a '{DomainKey}' value. Either the schema ") +
            "never reached the prompt, every reply failed to parse, or IsKeyAllowed rejected the " +
            "key the model chose. All three make the funded run pointless.");

        // GATE 2: the values are RIGHT, not merely present. This is the gate the counting pass
        // cannot supply and the reason this pilot exists: well-formed nonsense costs exactly as
        // much as useful metadata and is indistinguishable in any figure downstream.
        Assert.True(
            correct > 0,
            FormattableString.Invariant(
                $"{extracted} chunks carried a '{DomainKey}' value and none matched the corpus the ") +
            "chunk came from. The model is returning parseable metadata that describes nothing, " +
            "which a well-formedness check alone would have reported as a success.");
    }

    /// <summary>Runs the real ingest behaviour over one corpus slice and scores what it wrote.</summary>
    private static async Task<(int Extracted, int Correct, List<string> Values)> ExtractAsync(
        IChatClient client, IReadOnlyList<TextChunk> chunks, string expected, CancellationToken ct)
    {
        var behavior = new LlmMetadataExtractionBehavior
        {
            ChatClient = client,
            ExtractionOptions = new LlmMetadataExtractionOptions { Schema = DomainSchema },
        };

        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("pilot"),
            FileName = "pilot.txt",
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
        var values = new List<string>();

        foreach (var chunk in ctx.Chunks)
        {
            if (!chunk.Metadata.TryGetValue(DomainKey, out var value))
                continue;

            var text = value.StringValue ?? string.Empty;
            extracted++;
            values.Add(text);

            if (text.Contains(expected, StringComparison.OrdinalIgnoreCase))
                correct++;
        }

        return (extracted, correct, values);
    }

    /// <summary>Chunks a slice of one corpus through the real Real-protocol chunker.</summary>
    private static async Task<IReadOnlyList<TextChunk>> Slice(
        string datasetName, string cacheDirectory, CancellationToken ct)
    {
        var descriptor = BeirDatasetDescriptor.ByName(datasetName);
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);

        // A slice of DOCUMENTS, chunked, then a slice of the resulting units -- chunking the whole
        // corpus to use sixty units would spend minutes to save nothing.
        var documents = dataset.Documents.Take(ChunksPerCorpus).ToList();
        var units = await BeirRealChunkingTests.ChunkAsync(documents, ct);

        return units.Take(ChunksPerCorpus).ToList();
    }

    // NOTE: Mode and HasEntries duplicate BeirSelfQueryPilotTests', deliberately and temporarily.
    // PR #469 introduces a shared SelfQueryGate holding both; this branch is based on main so that
    // type does not exist here yet. Collapse these into it once #469 lands -- HasEntries in
    // particular encodes a correction (the cache SHARDS into subdirectories, so a non-recursive
    // enumeration reports a full cache as empty) and two copies is two chances to lose it.
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
