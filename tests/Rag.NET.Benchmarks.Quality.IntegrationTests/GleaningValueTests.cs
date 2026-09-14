using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.Graph;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// What the gleaning pass actually adds, replayed from the extraction cache at no spend.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/615">#615</see>.
/// <c>GraphRagOptions.GleaningPasses</c> defaults to 1, so every ingested chunk costs a second model
/// call, and that call is <b>66.8% of the input tokens ingestion spends per chunk</b> — measured for
/// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/153">#153</see>: the initial
/// extraction is 192 tokens against gleaning's 386, because gleaning re-sends the chunk <i>and</i>
/// the accumulated state. Nothing in this repository had measured what the second call buys.
/// </para>
/// <para>
/// <b>The real behaviour produces the prompts; this only intercepts them.</b> An earlier version
/// rebuilt both prompts from <c>GraphRagOptions</c>' defaults and looked them up directly. It hit
/// <b>nothing</b> — the cache is keyed on <c>GraphExtractionPrompt.Render</c> of the message list,
/// which prefixes the role, and a key differing by one character misses every entry. Driving
/// <c>GraphRagSliceIngestion.ExtractAsync</c> through a recording client removes that whole class of
/// error: the prompts are the ones the extraction tool sent, because the same code sends them.
/// </para>
/// <para>
/// <b>Which call is which is decided by content, not by order.</b> The gleaning prompt is the one
/// carrying <see cref="GleaningMarker"/>. Counting calls would assume chunks are processed one at a
/// time, which is a property of today's implementation rather than of the question being asked.
/// </para>
/// <para>
/// <b>Reports; asserts only that it measured something.</b> What counts as too little lift for a
/// doubled call is a judgement about recall against cost.
/// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/121">#121</see> — extraction
/// produced zero entities for the package's entire life without a test failing — is why the number
/// belongs on a transcript a human reads rather than behind a bound nobody revisits.
/// </para>
/// </remarks>
public sealed class GleaningValueTests
{
    /// <summary>The sentence only the gleaning prompt carries.</summary>
    private const string GleaningMarker =
        "You previously extracted entities and relationships from this text.";

    /// <summary>Articles to replay. Bounded so the case stays in seconds.</summary>
    private const int Articles = 60;

    /// <summary>Below this, the replay is broken rather than the cache empty.</summary>
    private const double LowestCredibleHitRate = 0.80;

    private readonly ITestOutputHelper _output;

    public GleaningValueTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Counts what the gleaning call adds over the extraction call.</summary>
    [Fact]
    public async Task TheGleaningPass_AddsEntitiesAndRelationshipsThatAreCounted()
    {
        Assert.SkipUnless(
            BeirHarness.IsDatasetCacheProvisioned(out var cacheDirectory),
            "Set RAGNET_BEIR_CACHE to the cache holding graph-extractions to measure what gleaning "
            + "adds." + BeirProvisioningHint.Describe());

        var cache = new GraphExtractionCache(
            cacheDirectory,
            GraphExtractionModelIdentity.For(GraphExtractionModelIdentity.ExtractionTemperature),
            GraphExtractionCacheMode.RefuseOnMiss);

        Assert.SkipUnless(
            Directory.Exists(cache.EntryDirectory),
            $"No extraction cache in {cache.EntryDirectory}. Download the published bundle — see "
            + "docs/reference/ci.md — or run the generation tool.");

        var ct = TestContext.Current.CancellationToken;
        var dataset = await BeirHarness.LoadAsync(
            BeirDatasetDescriptor.ByName(MultiHopRagSlice.DatasetName),
            cacheDirectory,
            BeirLoader.DefaultTitleTextSeparator,
            ct);

        var selection = GraphExtractionCorpusSelection.Select(
            dataset, GraphExtractionCorpus.Slice, Articles);

        var recorder = new ReplayRecorder(cache);
        var options = GraphRagSliceIngestion.CreateOptions();
        using var embedder = new StubEmbeddingGenerator();

        for (var i = 0; i < selection.Documents.Count; i++)
        {
            await using var graphStore = new SqliteGraphStore(":memory:");
            _ = await GraphRagSliceIngestion.ExtractAsync(
                selection.Documents[i],
                await GraphRagSliceIngestion.ChunkAsync(selection.Documents[i], ct),
                recorder,
                embedder,
                graphStore,
                options,
                ct);
        }

        _output.WriteLine(recorder.Describe(selection.Documents.Count, options.GleaningPasses));

        Assert.True(
            recorder.ExtractionCalls > 0,
            "No extraction call was intercepted, so nothing was measured.");

        Assert.True(
            recorder.HitRate >= LowestCredibleHitRate,
            $"Only {recorder.HitRate:P1} of intercepted prompts were in the cache, below " +
            $"{LowestCredibleHitRate:P0}. That is a broken replay rather than a finding: with a " +
            "cache filled over this corpus, every prompt the same code sends should be present. " +
            "Do not read the lift as a measurement until this passes.");
    }

    /// <summary>
    /// Replays cached replies and tallies extraction against gleaning.
    /// </summary>
    /// <remarks>
    /// A miss returns an empty extraction rather than throwing, the shape
    /// <c>GraphExtractionPlanProbe</c> uses, so one absent entry cannot abort a run whose purpose is
    /// to report how many were absent.
    /// </remarks>
    private sealed class ReplayRecorder : IChatClient
    {
        private const string EmptyExtraction = """{"entities": [], "relationships": []}""";

        private static readonly JsonSerializerOptions Wire =
            new() { PropertyNameCaseInsensitive = true };

        private readonly GraphExtractionCache _cache;

        public ReplayRecorder(GraphExtractionCache cache) => _cache = cache;

        public int ExtractionCalls { get; private set; }

        public int GleaningCalls { get; private set; }

        public int Misses { get; private set; }

        public int FirstEntities { get; private set; }

        public int FirstRelationships { get; private set; }

        public int AddedEntities { get; private set; }

        public int AddedRelationships { get; private set; }

        public int EmptyGleanings { get; private set; }

        public int AddedBlankNames { get; private set; }

        public int AddedEntitiesAlreadyFound { get; private set; }

        public int AddedRelationshipsAlreadyFound { get; private set; }

        /// <summary>Names the first pass returned for the chunk being processed.</summary>
        /// <remarks>
        /// <b>Raw counts overstate what gleaning contributes.</b> The behaviour appends the gleaned
        /// lists to the first pass's without deduplicating, so an entity the model simply repeats
        /// counts as an addition. Comparing against the immediately preceding extraction reply
        /// separates a genuinely new entity from a restated one — and that distinction is the whole
        /// difference between "gleaning finds 44% more relationships" and "gleaning returns 44% more
        /// rows".
        /// </remarks>
        private readonly HashSet<string> _entitiesFromFirstPass = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _relationshipsFromFirstPass = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets the share of intercepted prompts the cache held.</summary>
        public double HitRate
        {
            get
            {
                var total = ExtractionCalls + GleaningCalls;
                return total == 0 ? 0 : (double)(total - Misses) / total;
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            var prompt = GraphExtractionPrompt.Render(new List<ChatMessage>(messages));
            var isGleaning = prompt.Contains(GleaningMarker, StringComparison.Ordinal);
            var stored = _cache.TryGet(prompt);

            if (stored is null)
            {
                Misses++;
            }

            if (isGleaning)
            {
                GleaningCalls++;
            }
            else
            {
                ExtractionCalls++;
            }

            Tally(isGleaning, Parse(stored));

            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, stored ?? EmptyExtraction)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Nothing in GraphRAG streams.");

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }

        /// <summary>Renders the tally.</summary>
        /// <param name="articles">How many articles were replayed.</param>
        /// <param name="passes">The configured gleaning passes.</param>
        /// <returns>The report.</returns>
        public string Describe(int articles, int passes)
        {
            var entityLift = FirstEntities == 0 ? 0 : (double)AddedEntities / FirstEntities;
            var relationshipLift =
                FirstRelationships == 0 ? 0 : (double)AddedRelationships / FirstRelationships;
            var quiet = GleaningCalls == 0 ? 0 : (double)EmptyGleanings / GleaningCalls;
            var repeatedEntities =
                AddedEntities == 0 ? 0 : (double)AddedEntitiesAlreadyFound / AddedEntities;
            var repeatedRelationships = AddedRelationships == 0
                ? 0 : (double)AddedRelationshipsAlreadyFound / AddedRelationships;
            var novelEntityLift = FirstEntities == 0
                ? 0 : (double)(AddedEntities - AddedEntitiesAlreadyFound) / FirstEntities;
            var novelRelationshipLift = FirstRelationships == 0
                ? 0 : (double)(AddedRelationships - AddedRelationshipsAlreadyFound) / FirstRelationships;

            return FormattableString.Invariant($"""
                articles                {articles} at GleaningPasses = {passes}
                calls                   {ExtractionCalls} extraction, {GleaningCalls} gleaning, {Misses} missed
                cache hit rate          {HitRate:P1}   <- the replay guard

                first pass              {FirstEntities} entities, {FirstRelationships} relationships
                gleaning added          {AddedEntities} entities, {AddedRelationships} relationships
                lift                    {entityLift:P1} entities, {relationshipLift:P1} relationships

                of what gleaning returned, ALREADY in the first pass:
                  entities              {AddedEntitiesAlreadyFound} of {AddedEntities} ({repeatedEntities:P1})
                  relationships         {AddedRelationshipsAlreadyFound} of {AddedRelationships} ({repeatedRelationships:P1})
                NOVEL lift              {novelEntityLift:P1} entities, {novelRelationshipLift:P1} relationships

                gleaning calls returning nothing   {EmptyGleanings} of {GleaningCalls} ({quiet:P1})
                added entities with a blank name   {AddedBlankNames}
                """);
        }

        /// <summary>Adds one reply to the right bucket.</summary>
        /// <param name="isGleaning">Whether the reply answered a gleaning prompt.</param>
        /// <param name="parsed">The parsed reply, or null.</param>
        private void Tally(bool isGleaning, Extraction? parsed)
        {
            if (parsed is null)
            {
                return;
            }

            if (!isGleaning)
            {
                FirstEntities += parsed.Entities.Count;
                FirstRelationships += parsed.Relationships.Count;

                _entitiesFromFirstPass.Clear();
                _relationshipsFromFirstPass.Clear();
                foreach (var entity in parsed.Entities)
                {
                    _ = _entitiesFromFirstPass.Add(entity.Name);
                }

                foreach (var relationship in parsed.Relationships)
                {
                    _ = _relationshipsFromFirstPass.Add(Key(relationship));
                }

                return;
            }

            AddedEntities += parsed.Entities.Count;
            AddedRelationships += parsed.Relationships.Count;

            foreach (var entity in parsed.Entities)
            {
                if (_entitiesFromFirstPass.Contains(entity.Name))
                {
                    AddedEntitiesAlreadyFound++;
                }
            }

            foreach (var relationship in parsed.Relationships)
            {
                if (_relationshipsFromFirstPass.Contains(Key(relationship)))
                {
                    AddedRelationshipsAlreadyFound++;
                }
            }

            if (parsed.Entities.Count == 0 && parsed.Relationships.Count == 0)
            {
                EmptyGleanings++;
            }

            foreach (var entity in parsed.Entities)
            {
                if (string.IsNullOrWhiteSpace(entity.Name))
                {
                    AddedBlankNames++;
                }
            }
        }

        /// <summary>Identifies a relationship by its endpoints, as the graph store merges them.</summary>
        /// <param name="relationship">The relationship.</param>
        /// <returns>The comparison key.</returns>
        private static string Key(Relationship relationship) =>
            relationship.Source + " " + relationship.Target;

        /// <summary>Parses a cached reply, tolerating the fenced form models return.</summary>
        /// <param name="reply">The cached text, or null on a miss.</param>
        /// <returns>The extraction, or null when absent or unparseable.</returns>
        private static Extraction? Parse(string? reply)
        {
            if (reply is null)
            {
                return null;
            }

            var start = reply.IndexOf('{', StringComparison.Ordinal);
            var end = reply.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<Extraction>(reply[start..(end + 1)], Wire);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>The wire shape of an extraction, mirroring <c>Rag.NET.GraphRag</c>'s internal DTO.</summary>
    /// <remarks>
    /// Duplicated rather than referenced because the production types are <c>internal</c>, and
    /// widening a package's surface so a measurement can read it is the wrong trade. Only the reply
    /// is parsed here — the prompts come from the real behaviour — so a mismatch would understate
    /// counts rather than corrupt a cache lookup.
    /// </remarks>
    private sealed record Extraction
    {
        [JsonPropertyName("entities")]
        public IReadOnlyList<Entity> Entities { get; init; } = [];

        [JsonPropertyName("relationships")]
        public IReadOnlyList<Relationship> Relationships { get; init; } = [];
    }

    /// <summary>An extracted entity, on the wire.</summary>
    private sealed record Entity
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";
    }

    /// <summary>An extracted relationship, on the wire.</summary>
    private sealed record Relationship
    {
        [JsonPropertyName("source")]
        public string Source { get; init; } = "";

        [JsonPropertyName("target")]
        public string Target { get; init; } = "";
    }
}
