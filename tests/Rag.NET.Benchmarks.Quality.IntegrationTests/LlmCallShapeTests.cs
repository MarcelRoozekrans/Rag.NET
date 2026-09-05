using System.Text;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.GraphRag;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Memory;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.SelfQuery;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Prices the five unmeasured LLM features by COUNTING their calls, without spending anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why counting rather than estimating.</b> This project has mispriced a run twice, both times
/// the same way: by omitting a call category. The RAPTOR plan counted answers and omitted tree
/// construction; the answer-engine model omitted FLARE's per-sentence confidence scorer and landed
/// ~70,000 calls short. Both were arithmetic over a reading of the code. These tests run the real
/// components against a counting <see cref="IChatClient"/> and report what they actually do, which
/// is the method the answer-engine design settled on after its own model was corrected.
/// </para>
/// <para>
/// <b>The fake has to answer plausibly or it undercounts, which is the trap.</b>
/// <see cref="DeepResearchRetriever"/> treats an unreadable sufficiency response as "sufficient" and
/// leaves the loop — so a fake returning "an answer." measures ONE call where the real thing makes
/// <see cref="DeepResearchOptions.MaxDepth"/>. A cost model built on that fake would understate the
/// feature threefold. <see cref="DeepResearch_ShortCircuitsOnAnUnparseableReply"/> pins that
/// difference so nobody simplifies the scripted client back into a stub.
/// </para>
/// <para>
/// <b>What these tests do NOT establish.</b> They measure calls and the characters that go into and
/// out of them. Characters are not tokens: the conversion below is the conventional 4-characters-
/// per-token approximation, named as an approximation everywhere it is used, and the repository has
/// no GPT tokeniser to do better. They also measure ONE unit of work per feature — the scope
/// multipliers are declared inputs, not measurements, and are marked as such in the table.
/// </para>
/// </remarks>
public sealed class LlmCallShapeTests(ITestOutputHelper output)
{
    /// <summary>Calls per query, MEASURED by <see cref="DeepResearch_MakesOneCallPerDepth"/>.</summary>
    private const int DeepResearchCallsPerQuery = 3;

    /// <summary>Calls per chunk, MEASURED by <see cref="LlmMetadataExtraction_MakesOneCallPerChunk"/>.</summary>
    private const int ExtractionCallsPerChunk = 1;

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task DeepResearch_MakesOneCallPerDepth()
    {
        var options = new DeepResearchOptions { MaxDepth = 3, SubQueryCount = 3 };
        using var client = ScriptedCountingChatClient.AlwaysInsufficient(options.SubQueryCount);
        var retriever = new DeepResearchRetriever(new StubRetriever(), client, options);

        var result = await retriever.RetrieveAsync(
            "a research question", new RetrievalOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.True(
            result.IsSuccess,
            result.IsSuccess ? string.Empty : $"the retriever failed: {result.Error}");

        // One sufficiency call per depth. Note this is ONE call, not two: the XML doc on
        // DeepResearchOptions says "each sufficiency check and sub-query generation is an LLM call",
        // which reads as two per iteration -- CheckSufficiencyAsync returns both in a single
        // response. Costing from the doc rather than the code would have doubled this feature.
        Assert.Equal(options.MaxDepth, client.Calls);
        Assert.Equal(DeepResearchCallsPerQuery, client.Calls);

        Report("Deep Research Loop", "query", client);
    }

    [Fact]
    public async Task DeepResearch_ShortCircuitsOnAnUnparseableReply()
    {
        // The undercount this suite exists to prevent, pinned as a behaviour rather than a comment.
        var options = new DeepResearchOptions { MaxDepth = 3 };
        using var client = ScriptedCountingChatClient.Returning("an answer.");
        var retriever = new DeepResearchRetriever(new StubRetriever(), client, options);

        _ = await retriever.RetrieveAsync(
            "a research question", new RetrievalOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, client.Calls);
        Assert.NotEqual(options.MaxDepth, client.Calls);
    }

    [Fact]
    public async Task LlmMetadataExtraction_MakesOneCallPerChunk()
    {
        const int chunks = 7;
        using var client = ScriptedCountingChatClient.Returning("""{"topic":"finance"}""");

        var behavior = new LlmMetadataExtractionBehavior
        {
            ChatClient = client,
            ExtractionOptions = new LlmMetadataExtractionOptions(),
        };

        var ctx = IngestionContextWith(chunks);
        await behavior.HandleAsync(
            ctx, TestContext.Current.CancellationToken,
            static (c, _) => ValueTask.FromResult(
                new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        // Per CHUNK, not per document -- the single most important number in the model, because it
        // multiplies by the corpus's chunk count rather than its document count. On SciFact's Real
        // protocol that is 20,155 rather than 5,183, a 3.9x difference in the dominant term.
        Assert.Equal(chunks, client.Calls);
        Assert.Equal(ExtractionCallsPerChunk * chunks, client.Calls);

        Report("LLM Metadata Extraction", "chunk", client, units: chunks);
    }

    [Fact]
    public async Task SelfQuery_MakesOneCallPerQuery()
    {
        using var client = ScriptedCountingChatClient.Returning(
            // filters is an ARRAY of {key, value} objects. An object here throws out of
            // SelfQueryBehavior rather than degrading -- see SelfQuery_AnObjectShapedFilterThrows.
            """{"query":"rewritten","filters":[{"key":"topic","value":"finance"}]}""");

        var behavior = new SelfQueryBehavior
        {
            ChatClient = client,
            SelfQueryOptions = new SelfQueryOptions(),
        };

        var ctx = new RetrievalContext
        {
            Query = "a question that mentions a filterable attribute",
            Options = new RetrievalOptions { TopK = 10, UseSelfQuery = true },
        };

        _ = await behavior.HandleAsync(
            ctx, TestContext.Current.CancellationToken,
            static (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([]));

        Assert.Equal(1, client.Calls);

        Report("Self-Query", "query", client);
    }

    [Fact]
    public async Task SelfQuery_AnObjectShapedFilterDegrades_RatherThanThrowingOutOfRetrieval()
    {
        // FOUND BY THE COUNTING PASS, before any money was spent on the Self-Query run.
        //
        // The behaviour expects "filters" to be an ARRAY of {key, value} objects. A model returning
        // the more natural {"topic":"finance"} makes EnumerateArray() throw
        // InvalidOperationException -- and only JsonException is caught, so it escapes HandleAsync
        // and fails the whole retrieval. The comment above the catch says a previous fix stopped
        // malformed replies from "disabling self-query for the request with only a warning", so
        // degrading is the stated intent; throwing out of retrieval is not.
        //
        // This is not a hypothetical shape. "filters" as an object is what a schema-free prompt
        // most often gets back, which means the funded run would have crashed on ordinary replies.
        using var client = ScriptedCountingChatClient.Returning(
            """{"query":"rewritten","filters":{"topic":"finance"}}""");

        var behavior = new SelfQueryBehavior
        {
            ChatClient = client,
            SelfQueryOptions = new SelfQueryOptions(),
        };

        var ctx = new RetrievalContext
        {
            Query = "a question",
            Options = new RetrievalOptions { TopK = 10, UseSelfQuery = true },
        };

        var results = await behavior.HandleAsync(
            ctx, TestContext.Current.CancellationToken,
            static (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([]));

        Assert.NotNull(results);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task MindMap_MakesOneCallPerDocument()
    {
        using var client = ScriptedCountingChatClient.Returning(
            """{"name":"root","children":[{"name":"a","children":[]}]}""");

        var extractor = new MindMapExtractor(client, graphStore: null, new MindMapOptions());

        _ = await extractor.ExtractAsync(
            "a document body", "doc-1", TestContext.Current.CancellationToken);

        Assert.Equal(1, client.Calls);

        Report("Mind-Map Extractor", "document", client);
    }

    [Fact]
    public async Task ConversationMemory_MakesOneSummaryCallPerProcessedHistory()
    {
        using var client = ScriptedCountingChatClient.Returning("a summary of the conversation.");

        var pipeline = new ConversationMemoryPipeline(
            new ConversationMemoryOptions { UseSummary = true, MaxExchanges = 2 },
            client,
            logger: null);

        List<ChatMessage> history = [];
        for (var i = 0; i < 8; i++)
        {
            history.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"turn {i}"));
        }

        _ = await pipeline.ProcessAsync(history, TestContext.Current.CancellationToken);

        // One summary per processed history, not per turn -- the trimmed remainder is summarised
        // once. A per-turn reading would have overstated this feature by the conversation length.
        Assert.Equal(1, client.Calls);

        Report("Conversational Memory", "processed history", client);
    }


    [Fact]
    public void TheCostModel_PricesTheFiveFeaturesFromMeasuredCallShapes()
    {
        // PROVENANCE, kept separate because mixing these is how this project mispriced runs twice:
        //   calls per unit  -- MEASURED by the tests above, and pinned by the constants they assert.
        //   input overhead  -- MEASURED above (prompt template minus this file's tiny fixture text).
        //   input content   -- KNOWN from the corpora: a Real-protocol chunk is ~256 tokens.
        //   output size     -- SAMPLED from 465,260 real cached calls under ~/.cache/ragnet-beir:
        //                      extraction responses average 1,268 bytes, community reports 2,163.
        //                      It is NOT measurable here: the scripted client's reply length is
        //                      whatever this file chose, so counting it would measure the fixture.
        //   scope           -- DECLARED. These are scoping decisions, not facts, and each one is
        //                      the operator's to change; the table moves linearly with them.
        //
        // Rate: $0.37/M blended, the project's one real anchor (~$9 for 24.3M tokens of extraction
        // and community reports). OpenRouter's gpt-4o-mini card was never checked against the
        // $0.15/$0.60 split those runs assumed -- flagged in the answer-engine design too.
        const double BlendedPricePerMillionTokens = 0.37;
        const double CharsPerToken = 4.0;

        (string Feature, int Calls, int CharsPerCall)[] rows =
        [
            // 300 judged SciFact queries x 3 calls, ~200 char prompt + a small JSON reply.
            ("Deep Research Loop", 300 * DeepResearchCallsPerQuery, 200 + 10_240 + 200),

            // 20,155 Real-protocol chunks x 1 call. The dominant term, and the reason "per chunk"
            // rather than "per document" was worth measuring: per document it would be 5,183.
            ("LLM Metadata Extraction", 20_155 * ExtractionCallsPerChunk, 189 + 1_024 + 1_268),

            // 300 judged SciFact queries x 1 call.
            ("Self-Query", 300, 185 + 120 + 200),

            // ~3,000 MultiHop-RAG documents x 1 call.
            ("Mind-Map Extractor", 3_000, 460 + 4_000 + 1_268),

            // ~20 conversations x 10 turns, one summary per processed history.
            ("Conversational Memory", 200, 88 + 2_000 + 500),
        ];

        var total = 0.0;
        foreach (var (feature, calls, charsPerCall) in rows)
        {
            var tokens = (double)calls * charsPerCall / CharsPerToken;
            var cost = tokens / 1_000_000 * BlendedPricePerMillionTokens;
            total += cost;

            _output.WriteLine(FormattableString.Invariant(
                $"{feature,-26} {calls,7:N0} calls  {tokens,12:N0} tokens  ${cost,7:F2}"));
        }

        _output.WriteLine(FormattableString.Invariant($"{"TOTAL",-26} {"",7} {"",12}  ${total,7:F2}"));

        // A ceiling, not a prediction. If a call shape changes -- Deep Research going to two calls
        // per depth, extraction moving per-attribute -- this trips and the model is known stale
        // BEFORE the money is spent, which is the entire point of counting instead of estimating.
        Assert.True(
            total < 10.0,
            FormattableString.Invariant(
                $"the five features now price at ${total:F2}, over the $10 ceiling this model was ") +
            "written against. A call shape changed; re-measure before funding the runs.");
    }

    /// <summary>Prints one feature's measured shape for the cost table.</summary>
    private void Report(string feature, string unit, ScriptedCountingChatClient client, int units = 1)
    {
        var callsPerUnit = (double)client.Calls / units;
        var charsPerUnit = (double)(client.InputChars + client.OutputChars) / units;

        _output.WriteLine(FormattableString.Invariant($"""
            {feature}: {callsPerUnit:F2} call(s) per {unit}
              in {client.InputChars} chars, out {client.OutputChars} chars over {client.Calls} call(s)
              ~{charsPerUnit / 4:F0} tokens per {unit} (chars/4 -- an APPROXIMATION, not a tokeniser)
            """));
    }

    private static IngestionContext IngestionContextWith(int chunks)
    {
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("doc1"),
            FileName = "doc1.txt",
        };

        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = metadata,
            GetNextBm25DocId = () => 0,
        };

        for (var i = 0; i < chunks; i++)
        {
            ctx.Chunks.Add(new TextChunk
            {
                Text = FormattableString.Invariant($"chunk {i} body text"),
                DocumentId = metadata.DocumentId,
                ChunkIndex = i,
            });
        }

        return ctx;
    }

    /// <summary>A retriever that returns one result without calling a model.</summary>
    private sealed class StubRetriever : IRetriever
    {
        public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success([]));
    }

    /// <summary>
    /// Counts calls and the characters crossing them, answering with a scripted reply.
    /// </summary>
    /// <remarks>
    /// The reply is scripted per feature because a generic one changes the counts: see
    /// <see cref="DeepResearch_ShortCircuitsOnAnUnparseableReply"/>, where a stub reply measures a
    /// third of the real call count. Counting a component that took an error path is measuring the
    /// error path.
    /// </remarks>
    private sealed class ScriptedCountingChatClient : IChatClient
    {
        private readonly Func<int, string> _reply;
        private int _calls;
        private int _inputChars;
        private int _outputChars;

        private ScriptedCountingChatClient(Func<int, string> reply) => _reply = reply;

        public int Calls => Volatile.Read(ref _calls);

        public int InputChars => Volatile.Read(ref _inputChars);

        public int OutputChars => Volatile.Read(ref _outputChars);

        public static ScriptedCountingChatClient Returning(string reply) =>
            new(_ => reply);

        /// <summary>
        /// Answers every sufficiency check with "not sufficient" plus sub-queries, so the deep
        /// research loop runs its full depth instead of leaving on the first reply.
        /// </summary>
        public static ScriptedCountingChatClient AlwaysInsufficient(int subQueryCount)
        {
            var subQueries = string.Join(
                ',',
                Enumerable.Range(0, subQueryCount).Select(i => FormattableString.Invariant($"\"sub {i}\"")));

            return new(_ => FormattableString.Invariant(
                $$"""{"sufficient":false,"subQueries":[{{subQueries}}]}"""));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);

            var input = new StringBuilder();
            foreach (var message in messages)
            {
                input.Append(message.Text);
            }

            var reply = _reply(call);
            Interlocked.Add(ref _inputChars, input.Length);
            Interlocked.Add(ref _outputChars, reply.Length);

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("None of the five features stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
