using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Graph;
using Rag.NET.GraphRag;
using Rag.NET.GraphRag.LocalSearch;
using Rag.NET.Models;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.GraphRag.Tests.LocalSearch;

/// <summary>
/// Drives local search end to end over a real graph store, a real vector store and a stub model.
/// </summary>
/// <remarks>
/// <para>
/// The context builder is unit-tested against the specification elsewhere. What is only testable
/// here is the <i>collection</i> — that the entity a query matches brings its relationships, its
/// community's report and its source chunks with it. That collection is step 3 of the 2026-03-30
/// design, the step that never shipped, so a test that it happens at all is the point.
/// </para>
/// <para>
/// Stores are real, not substitutes: the interesting failures are in what gets asked of them —
/// selection order lost on the way back from a batch read, degrees asked for the wrong names — and
/// a substitute that returns whatever it was told to returns those failures unchanged.
/// </para>
/// </remarks>
public sealed class GraphRagSearchTests
{
    [Fact]
    public async Task ASelectedEntityBringsItsRelationshipsReportAndSourceChunks()
    {
        await using var fixture = await Fixture.CreateAsync();

        var context = await fixture.Search.BuildLocalContextAsync(
            "spectroscopy", TestContext.Current.CancellationToken);

        Assert.Contains("-----Entities-----", context.Text, StringComparison.Ordinal);
        Assert.Contains("ÅNGSTRÖM", context.Text, StringComparison.OrdinalIgnoreCase);

        // The three sections that did not exist before this work. Each is a separate claim.
        Assert.True(context.Relationships.Rendered > 0, "No relationships reached the context.");
        Assert.True(context.Reports.Rendered > 0, "No community report reached the context.");
        Assert.True(context.Sources.Rendered > 0, "No source chunks reached the context.");
    }

    /// <remarks>
    /// Source chunks are the article text, which lives in the document store and is reached only by
    /// key. Asserted on the text rather than on the count, because a Sources section containing the
    /// entity descriptions again would satisfy a count and be the wrong material entirely.
    /// </remarks>
    [Fact]
    public async Task TheSourcesSectionHoldsArticleTextRatherThanGraphText()
    {
        await using var fixture = await Fixture.CreateAsync();

        var context = await fixture.Search.BuildLocalContextAsync(
            "spectroscopy", TestContext.Current.CancellationToken);

        Assert.Contains("solar spectrum in 1868", context.Text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <b>The regression that matters most.</b> A store with no keyed read leaves Sources empty —
    /// half the token budget — and the failure is otherwise invisible: the answer is simply thinner.
    /// Everything else must still be assembled.
    /// </remarks>
    [Fact]
    public async Task AStoreWithoutAKeyedReadLosesOnlyTheSourcesSection()
    {
        await using var fixture = await Fixture.CreateAsync(documentStore: new NoLookupStore());

        var context = await fixture.Search.BuildLocalContextAsync(
            "spectroscopy", TestContext.Current.CancellationToken);

        Assert.Equal(0, context.Sources.Rendered);
        Assert.True(context.Entities.Rendered > 0, "Entities should still be assembled.");
        Assert.True(context.Relationships.Rendered > 0, "Relationships should still be assembled.");
    }

    /// <remarks>
    /// One entity mentioned in several documents has one entity chunk per document, all matching a
    /// query about it. Without de-duplication the selection is one entity repeated rather than ten
    /// distinct ones, and every downstream ordering keys off that list.
    /// </remarks>
    [Fact]
    public async Task AnEntityMentionedInSeveralDocumentsIsSelectedOnce()
    {
        await using var fixture = await Fixture.CreateAsync();

        var context = await fixture.Search.BuildLocalContextAsync(
            "physicist", TestContext.Current.CancellationToken);

        var rows = EntityRows(context.Text);
        Assert.Equal(rows.Distinct(StringComparer.OrdinalIgnoreCase).Count(), rows.Count);
    }

    /// <remarks>
    /// A query matching nothing produces an empty context rather than an exception, and an answer
    /// that says so rather than one invented from an empty table.
    /// </remarks>
    [Fact]
    public async Task AQueryThatMatchesNoEntityProducesAnEmptyContext()
    {
        await using var fixture = await Fixture.CreateAsync(emptyGraph: true);

        var context = await fixture.Search.BuildLocalContextAsync(
            "anything", TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, context.Text, StringComparer.Ordinal);
        Assert.Equal(0, context.TokenCount);
    }

    /// <remarks>
    /// The answer's citations name rows of the context tables, so returning the answer without the
    /// context returns citations that resolve to nothing.
    /// </remarks>
    [Fact]
    public async Task AnAnswerComesBackWithTheContextItsCitationsReferTo()
    {
        await using var fixture = await Fixture.CreateAsync();

        var answer = await fixture.Search.LocalSearchAsync(
            "spectroscopy", TestContext.Current.CancellationToken);

        Assert.Equal("stub answer", answer.Answer, StringComparer.Ordinal);
        Assert.Contains("-----Entities-----", answer.Context.Text, StringComparison.Ordinal);

        // The context tables reach the model, not just the caller.
        Assert.Contains("-----Entities-----", fixture.Model.LastSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("---Data tables---", fixture.Model.LastSystemPrompt, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <b>Upstream oversamples by 2 and does not truncate back</b>, so the default selects up to
    /// twice <c>TopKEntities</c>. Reproduced rather than corrected, and pinned here so that
    /// "correcting" it later is a deliberate act rather than a tidy-up.
    /// </remarks>
    [Fact]
    public async Task TheOversampleIsNotTruncatedBackToTopK()
    {
        await using var fixture = await Fixture.CreateAsync(
            configure: o => { o.TopKEntities = 1; o.EntityOversampleScaler = 2; });

        var context = await fixture.Search.BuildLocalContextAsync(
            "physicist", TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Entities.Rendered);
    }

    [Fact]
    public async Task SettingTheScalerToOneSelectsExactlyTopK()
    {
        await using var fixture = await Fixture.CreateAsync(
            configure: o => { o.TopKEntities = 1; o.EntityOversampleScaler = 1; });

        var context = await fixture.Search.BuildLocalContextAsync(
            "physicist", TestContext.Current.CancellationToken);

        Assert.Equal(1, context.Entities.Rendered);
    }

    /// <remarks>
    /// The three-argument entry point threads history into the assembled context the same way
    /// <see cref="LocalSearchContextBuilder"/> is already proven to (Task 3) — this pins that the
    /// entry point actually passes it through rather than dropping it on the floor.
    /// </remarks>
    [Fact]
    public async Task HistoryReachesTheAssembledContext()
    {
        await using var fixture = await Fixture.CreateAsync();

        var context = await fixture.Search.BuildLocalContextAsync(
            "And who measured it?",
            [new ConversationTurn(ConversationRole.User, "What did ÅNGSTRÖM work on?")],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, context.History.Rendered);
        Assert.Contains("What did ÅNGSTRÖM work on?", context.Text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Spec §9.5. Selection sees the history even though the rendered section is built separately —
    /// a follow-up question embeds to almost nothing on its own. <see cref="StubEmbedder"/> is a
    /// substitute whose arguments this test can capture, so it asserts directly on the embedded
    /// string rather than only on downstream entity selection.
    /// </remarks>
    [Fact]
    public async Task HistoryIsFoldedIntoTheQueryBeforeEntitySelection()
    {
        await using var fixture = await Fixture.CreateAsync();

        var context = await fixture.Search.BuildLocalContextAsync(
            "and who measured it?",
            [new ConversationTurn(ConversationRole.User, "What did ÅNGSTRÖM work on?")],
            TestContext.Current.CancellationToken);

        Assert.True(context.Entities.Rendered > 0, "the folded query selected no entities");

        var embedded = Assert.Single(fixture.Embedder.LastInput);
        Assert.Contains("and who measured it?", embedded, StringComparison.Ordinal);
        Assert.Contains("What did ÅNGSTRÖM work on?", embedded, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A single-turn history cannot distinguish newest-first from oldest-first, or last-N from
    /// first-N — this pins the ordering with three user turns and a cap below that count. Spec
    /// §9.5's <c>get_user_turns</c> walks the history reversed, collecting the most recent user
    /// turns and stopping at the cap; this deliberately disagrees with the rendered history
    /// section's oldest-first selection (Task 3) — both are upstream's behaviour in the same
    /// <c>build_context</c> call, not reconciled here.
    /// </remarks>
    [Fact]
    public async Task TheFoldTakesTheMostRecentUserTurnsNewestFirst()
    {
        await using var fixture = await Fixture.CreateAsync(
            configure: o => o.ConversationHistoryMaxTurns = 2);

        await fixture.Search.BuildLocalContextAsync(
            "and who measured it?",
            [
                new ConversationTurn(ConversationRole.User, "question one"),
                new ConversationTurn(ConversationRole.Assistant, "answer one"),
                new ConversationTurn(ConversationRole.User, "question two"),
                new ConversationTurn(ConversationRole.User, "question three"),
            ],
            TestContext.Current.CancellationToken);

        var embedded = Assert.Single(fixture.Embedder.LastInput);

        // The current query comes first, ahead of all folded history.
        Assert.Equal(0, embedded.IndexOf("and who measured it?", StringComparison.Ordinal));

        // Last-N (cap = 2): the two most recent user turns survive, the oldest does not.
        Assert.DoesNotContain("question one", embedded, StringComparison.Ordinal);
        Assert.Contains("question two", embedded, StringComparison.Ordinal);
        Assert.Contains("question three", embedded, StringComparison.Ordinal);

        // Newest first: "question three" (most recent) precedes "question two" (older) —
        // the opposite order from the rendered history section.
        var three = embedded.IndexOf("question three", StringComparison.Ordinal);
        var two = embedded.IndexOf("question two", StringComparison.Ordinal);
        Assert.True(three < two, "the newest question should appear before the older one");

        // User turns only: the assistant's reply never reaches the embedder.
        Assert.DoesNotContain("answer one", embedded, StringComparison.Ordinal);
    }

    /// <summary>Reads the entity column out of a rendered context.</summary>
    /// <param name="text">The rendered context.</param>
    /// <returns>Entity names in render order.</returns>
    private static List<string> EntityRows(string text)
    {
        var names = new List<string>();
        var inSection = false;

        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("-----", StringComparison.Ordinal))
            {
                inSection = string.Equals(line, "-----Entities-----", StringComparison.Ordinal);
                continue;
            }

            if (inSection && line.Contains('|', StringComparison.Ordinal) &&
                !line.StartsWith("id|", StringComparison.Ordinal))
            {
                names.Add(line.Split('|')[1]);
            }
        }

        return names;
    }

    /// <summary>A graph, a document store and a chunk store holding one small consistent corpus.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteGraphStore _graph = null!;
        private InMemoryVectorStore _chunks = null!;
        private IVectorStore _documents = null!;

        public GraphRagSearch Search { get; private set; } = null!;

        public StubChatClient Model { get; } = new();

        public StubEmbedder Embedder { get; private set; } = null!;

        /// <summary>Builds the fixture.</summary>
        /// <param name="documentStore">Override for the document store, to test capability absence.</param>
        /// <param name="emptyGraph">Whether to seed nothing, so nothing can be selected.</param>
        /// <param name="configure">Adjusts the local search options.</param>
        /// <returns>The fixture.</returns>
        public static async Task<Fixture> CreateAsync(
            IVectorStore? documentStore = null,
            bool emptyGraph = false,
            Action<LocalSearchContextOptions>? configure = null)
        {
            var ct = TestContext.Current.CancellationToken;
            var fixture = new Fixture
            {
                _graph = new SqliteGraphStore(":memory:"),
                _chunks = new InMemoryVectorStore(),
            };

            var documents = new InMemoryVectorStore();
            fixture._documents = documentStore ?? documents;

            var embedder = new StubEmbedder();
            fixture.Embedder = embedder;

            if (!emptyGraph)
            {
                await SeedGraphAsync(fixture._graph, ct);
                await SeedChunksAsync(fixture._chunks, embedder, ct);
                await SeedDocumentsAsync(documents, embedder, ct);
            }

            var options = new LocalSearchContextOptions();
            configure?.Invoke(options);

            fixture.Search = new GraphRagSearch(
                fixture._graph,
                new GraphChunkStore(fixture._chunks),
                fixture._documents,
                embedder,
                fixture.Model,
                options);

            return fixture;
        }

        /// <summary>Two physicists, an edge between them, and a community holding both.</summary>
        /// <param name="graph">The graph store.</param>
        /// <param name="ct">Cancellation.</param>
        private static async Task SeedGraphAsync(SqliteGraphStore graph, CancellationToken ct)
        {
            await graph.AddEntitiesAsync(
            [
                new GraphEntity("ÅNGSTRÖM", "Person", "Swedish physicist and spectroscopist")
                {
                    SourceChunkIds = ["article1_0"],
                },
                new GraphEntity("KELVIN", "Person", "British physicist")
                {
                    SourceChunkIds = ["article1_1"],
                },
            ], ct);

            await graph.AddRelationshipsAsync(
            [
                new GraphRelationship("ÅNGSTRÖM", "KELVIN", "corresponded with")
                {
                    SourceChunkIds = ["article1_0"],
                },
            ], ct);

            await graph.SetCommunitiesAsync(
            [
                new Community(1, 0, ["ÅNGSTRÖM", "KELVIN"], "Nineteenth-century spectroscopy"),
            ], ct);
        }

        /// <summary>The entity chunks local search selects from — one per document, as ingestion writes them.</summary>
        /// <param name="chunks">The graph chunk store.</param>
        /// <param name="embedder">Embedder.</param>
        /// <param name="ct">Cancellation.</param>
        private static async Task SeedChunksAsync(
            InMemoryVectorStore chunks, StubEmbedder embedder, CancellationToken ct)
        {
            await chunks.StoreAsync(
            [
                await EntityChunkAsync(embedder, "article1", -1, "ÅNGSTRÖM", "Swedish physicist and spectroscopist", ct),
                await EntityChunkAsync(embedder, "article1", -2, "KELVIN", "British physicist", ct),

                // The same entity again, from a second document — what ingestion actually produces,
                // and the reason selection has to de-duplicate.
                await EntityChunkAsync(embedder, "article2", -1, "ÅNGSTRÖM", "Swedish physicist, spectroscopy", ct),
            ], ct);
        }

        /// <summary>The article chunks the Sources section is built from.</summary>
        /// <param name="documents">The document store.</param>
        /// <param name="embedder">Embedder.</param>
        /// <param name="ct">Cancellation.</param>
        private static async Task SeedDocumentsAsync(
            InMemoryVectorStore documents, StubEmbedder embedder, CancellationToken ct)
        {
            await documents.StoreAsync(
            [
                await ArticleChunkAsync(embedder, "article1", 0, "Ångström mapped the solar spectrum in 1868.", ct),
                await ArticleChunkAsync(embedder, "article1", 1, "Kelvin defined the absolute temperature scale.", ct),
            ], ct);
        }

        /// <summary>Builds an entity chunk as <c>GraphEntityExtractionBehavior</c> writes them.</summary>
        /// <param name="embedder">Embedder.</param>
        /// <param name="documentId">Owning document.</param>
        /// <param name="index">Negative synthetic index.</param>
        /// <param name="name">Entity name.</param>
        /// <param name="description">Entity description, which is the chunk text.</param>
        /// <param name="ct">Cancellation.</param>
        /// <returns>The chunk.</returns>
        private static async Task<EmbeddedChunk> EntityChunkAsync(
            StubEmbedder embedder, string documentId, int index, string name, string description, CancellationToken ct)
        {
            var embedding = await embedder.GenerateAsync([description], cancellationToken: ct);
            return new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = description,
                    DocumentId = new DocumentId(documentId),
                    ChunkIndex = index,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["graph_type"] = "entity",
                        ["graph_entity_name"] = name,
                        ["graph_entity_type"] = "Person",
                    },
                },
                Embedding = embedding[0].Vector,
            };
        }

        /// <summary>Builds an article chunk.</summary>
        /// <param name="embedder">Embedder.</param>
        /// <param name="documentId">Owning document.</param>
        /// <param name="index">Position within it.</param>
        /// <param name="text">Chunk text.</param>
        /// <param name="ct">Cancellation.</param>
        /// <returns>The chunk.</returns>
        private static async Task<EmbeddedChunk> ArticleChunkAsync(
            StubEmbedder embedder, string documentId, int index, string text, CancellationToken ct)
        {
            var embedding = await embedder.GenerateAsync([text], cancellationToken: ct);
            return new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = text,
                    DocumentId = new DocumentId(documentId),
                    ChunkIndex = index,
                },
                Embedding = embedding[0].Vector,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await _graph.DisposeAsync();
            _chunks.Dispose();
            if (_documents is InMemoryVectorStore owned)
            {
                owned.Dispose();
            }
        }
    }

    /// <summary>
    /// A deterministic embedder: a bag-of-characters vector, so texts sharing words are near.
    /// </summary>
    /// <remarks>
    /// Deterministic rather than random because selection order decides the entity table's order
    /// and the source-chunk blocks, and a test asserting on either needs the same order every run.
    /// </remarks>
    private sealed class StubEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        /// <summary>The strings the most recent <see cref="GenerateAsync"/> call was given.</summary>
        public IReadOnlyList<string> LastInput { get; private set; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var input = values as IReadOnlyList<string> ?? [.. values];
            LastInput = input;

            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in input)
            {
                var vector = new float[26];
                foreach (var c in value.ToLowerInvariant())
                {
                    if (c is >= 'a' and <= 'z')
                    {
                        vector[c - 'a']++;
                    }
                }

                result.Add(new Embedding<float>(vector));
            }

            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Records the system prompt it was given and returns a fixed answer.</summary>
    private sealed class StubChatClient : IChatClient
    {
        public string LastSystemPrompt { get; private set; } = string.Empty;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var message in messages)
            {
                if (message.Role == ChatRole.System)
                {
                    LastSystemPrompt = message.Text;
                }
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub answer")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>A document store with no keyed read.</summary>
    private sealed class NoLookupStore : IVectorStore
    {
        public Task StoreAsync(
            IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding,
            Rag.NET.Models.Options.SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([]);

        public Task DeleteByDocumentIdAsync(
            string documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
