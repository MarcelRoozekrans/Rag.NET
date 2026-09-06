using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Rag.NET.Abstractions;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The conversation-memory cell: twenty multi-turn conversations built from real MultiHop-RAG text
/// run through the shipped <c>ConversationMemoryPipeline</c>, resolved from a real
/// <c>AddRagNet</c> container, with a real model writing the summaries.
/// </summary>
/// <remarks>
/// <para>
/// <b>It processes after every turn, which is what a caller does.</b> An answer engine calls
/// <see cref="IConversationMemory.ProcessAsync"/> before each request, so a ten-turn conversation
/// processes ten times and the window slides once per turn. Running it once per conversation would
/// exercise the trim on one history shape and call it multi-turn.
/// </para>
/// <para>
/// <b>THE GUARD IS THE POINT, because a failed summary is invisible.</b>
/// <c>GenerateSummaryAsync</c> catches every exception and returns <see langword="null"/>;
/// <c>ProcessAsync</c> then simply does not insert the summary message. The returned history still
/// looks correctly trimmed, the call still succeeds, and nothing distinguishes "nothing needed
/// summarising" from "the model was never reached". <see cref="AssertTheModelActuallySummarised"/>
/// refuses that run.
/// </para>
/// <para>
/// <b>That is not hypothetical.</b> On 2026-09-06 the deep-research cell reproduced its control
/// exactly with zero model calls, because its retriever swallowed a cache refusal as a verdict.
/// This pipeline has the same fail-open shape and differs only in passing <c>options: null</c>, so
/// it never sets the <c>ResponseFormat</c> the cache refused until that same day.
/// </para>
/// <para>
/// <b>Cost</b>: at most one call per processed turn that trims something — 200 across twenty
/// ten-turn conversations, about $0.05. Cached on disk; a re-run replays free.
/// </para>
/// </remarks>
public sealed class ConversationMemoryExerciseTests(ITestOutputHelper output)
{
    private const string GenerateVariable = "RAGNET_CONVERSATION_MEMORY_GENERATE";
    private const string ApiKeyVariable = "OPENROUTER_API_KEY";
    private const string CacheSubdirectory = "conversation-memory";
    private const string SummaryPrefix = "Summary of earlier conversation:";

    private const int Conversations = 20;
    private const int TurnsPerConversation = 10;

    /// <summary>Exchanges the window keeps, small enough that trimming starts early.</summary>
    private const int MaxExchanges = 3;

    private static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1");

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task ConversationMemory_OverTwentyMultiTurnConversations_SummarisesWhatItTrims()
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
            "nothing to replay and nothing may be spent.");

        var ct = TestContext.Current.CancellationToken;

        var descriptor = BeirDatasetDescriptor.ByName(MultiHopRagSlice.DatasetName);
        var dataset = await BeirHarness.LoadAsync(
            descriptor, cacheDirectory, BeirLoader.DefaultTitleTextSeparator, ct);

        var turns = BuildTurns(dataset);

        using var chat = OpenClient(cache, generating);
        await using var provider = BuildProvider(chat);
        var memory = provider.GetRequiredService<IConversationMemory>();

        var (processed, summarised, trimmedAway) = await RunConversationsAsync(memory, turns, ct);

        _output.WriteLine(FormattableString.Invariant($"""
            === conversation memory · {Conversations} conversations x {TurnsPerConversation} turns ===
            MaxExchanges {MaxExchanges}, UseSummary true.
            {processed} histories processed, {summarised} came back carrying a summary message.
            {trimmedAway} messages fell out of the window across the run.
            cache: {cache.Hits} hits, {cache.Misses} misses (misses are what was paid for).
            A summary that fails returns null and is simply not inserted, so the count above is the
            only thing separating a working run from one that never reached the model.
            """));

        AssertTheModelActuallySummarised(summarised, processed);
    }

    /// <summary>
    /// Refuses a run where nothing was ever summarised, before it is read as an exercise.
    /// </summary>
    /// <param name="summarised">Processed histories that came back carrying a summary message.</param>
    /// <param name="processed">Histories processed in total.</param>
    /// <remarks>
    /// With <see cref="MaxExchanges"/> at 3 and ten turns, most processed histories trim something
    /// and therefore ask for a summary. Zero summaries across the whole run means every call failed
    /// or none was made, and <c>ProcessAsync</c> reports neither.
    /// </remarks>
    private static void AssertTheModelActuallySummarised(int summarised, int processed)
    {
        Assert.True(
            processed > 0,
            "no history was processed at all, so there is nothing to judge.");

        Assert.True(
            summarised > 0,
            FormattableString.Invariant(
                $"none of {processed} processed histories came back with a summary message. ") +
            "GenerateSummaryAsync catches every exception and returns null, and ProcessAsync then " +
            "omits the message rather than failing, so a run that reached no model at all returns " +
            "correctly-trimmed histories and looks exactly like this. Check the cache's miss count " +
            "before concluding the model is at fault.");
    }

    /// <summary>Runs every conversation turn by turn, processing after each one.</summary>
    private static async Task<(int Processed, int Summarised, int TrimmedAway)> RunConversationsAsync(
        IConversationMemory memory,
        IReadOnlyList<(string User, string Assistant)> turns,
        CancellationToken ct)
    {
        var processed = 0;
        var summarised = 0;
        var trimmedAway = 0;

        for (var c = 0; c < Conversations; c++)
        {
            var history = new List<ChatMessage>();

            for (var t = 0; t < TurnsPerConversation; t++)
            {
                var turn = turns[((c * TurnsPerConversation) + t) % turns.Count];
                history.Add(new ChatMessage(ChatRole.User, turn.User));
                history.Add(new ChatMessage(ChatRole.Assistant, turn.Assistant));

                var result = await memory.ProcessAsync(history, ct);
                processed++;

                if (result.Count > 0
                    && result[0].Role == ChatRole.System
                    && (result[0].Text ?? string.Empty).StartsWith(SummaryPrefix, StringComparison.Ordinal))
                {
                    summarised++;
                }

                var carried = 0;
                for (var i = 0; i < result.Count; i++)
                {
                    if (result[i].Role != ChatRole.System)
                        carried++;
                }

                trimmedAway += Math.Max(0, history.Count - carried);
            }
        }

        return (processed, summarised, trimmedAway);
    }

    /// <summary>
    /// Builds the turns from real corpus text rather than invented chatter.
    /// </summary>
    /// <remarks>
    /// The user turns are MultiHop-RAG's own judged questions and the assistant turns are the
    /// opening of the slice's articles. Both are real text of the kind a summary would meet;
    /// synthetic filler would measure how well the model summarises filler.
    /// </remarks>
    private static IReadOnlyList<(string User, string Assistant)> BuildTurns(BeirDataset dataset)
    {
        var queries = MultiHopRagSlice.Queries(dataset.Queries);
        var documents = MultiHopRagSlice.Documents(dataset.Documents);

        Assert.True(
            queries.Count > 0 && documents.Count > 0,
            "the slice resolved no queries or no documents, so the conversations would be empty.");

        var turns = new List<(string, string)>(queries.Count);
        for (var i = 0; i < queries.Count; i++)
        {
            var text = documents[i % documents.Count].Text;
            var reply = text.Length <= 400 ? text : text[..400];
            turns.Add((queries[i].Text, reply));
        }

        return turns;
    }

    /// <summary>Builds a real container with the shipped memory pipeline registered.</summary>
    private static ServiceProvider BuildProvider(IChatClient chat)
    {
        var services = new ServiceCollection();
        services.AddSingleton(chat);
        services.AddRagNet(rag => rag.UseConversationMemory(new ConversationMemoryOptions
        {
            MaxExchanges = MaxExchanges,
            UseSummary = true,
        }));

        var provider = services.BuildServiceProvider();

        // The cell is meaningless if the container hands back something other than the shipped
        // pipeline, and that failure would be silent: any IConversationMemory returns a history.
        var memory = provider.GetRequiredService<IConversationMemory>();
        Assert.IsType<Rag.NET.Memory.ConversationMemoryPipeline>(memory);

        return provider;
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
