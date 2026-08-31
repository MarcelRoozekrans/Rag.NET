using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.AnswerEngines;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques.ContextualCompression;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Executes one LLM call per source chunk in parallel (map), filters "not found" responses,
/// then combines surviving partials in a single reduce call.
/// </summary>
public sealed class MapReduceAnswerEngine(
    IChatClient chatClient,
    ILogger<MapReduceAnswerEngine> logger,
    IConversationMemory? memory = null,
    IContextualCompressor? compressor = null) : IAnswerEngine
{
    private const string DefaultMapPrompt =
        "Using only the following text, answer this question as best you can.\n" +
        "If the text doesn't contain relevant information, say \"not found\".\n\n" +
        "Text:\n{chunk}\n\nQuestion: {query}";

    private const string DefaultReducePrompt =
        "Synthesize the following partial answers into a single coherent response.\n" +
        "Discard redundant or contradictory information.\n\n" +
        "Partial answers:\n{partials}\n\nQuestion: {query}";

    /// <summary>
    /// The map step's own protocol, appended after a caller's <see cref="RagOptions.SystemPrompt"/>
    /// so that a caller's formatting instruction cannot reshape the refusal sentinel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The refusal sentinel is load-bearing.</b> A map that finds nothing relevant replies
    /// <c>not found</c>, and those partials are dropped by an <b>exact</b> match before the reduce
    /// runs — see the filter in <see cref="AskAsync"/>. A caller system prompt that changes the
    /// shape of a reply defeats that match, and the refusals then reach the reduce as if they were
    /// content.
    /// </para>
    /// <para>
    /// <b>Measured 2026-08-30, with a transcript.</b> Under a caller prompt ending "end your reply
    /// with exactly this sentence: The answer to the question is …", six maps produced one correct
    /// answer and five refusals — but three of those refusals came back as
    /// <c>Not found. The answer to the question is "not found".</c>, which is not equal to
    /// <c>not found</c> and so survived the filter. The reduce saw one answer against three
    /// refusals, called it a contradiction, and <b>discarded the correct answer</b>. The engine had
    /// the answer and threw it away.
    /// </para>
    /// <para>
    /// Appended <b>last</b> so it is the most recent instruction the model reads, and applied to the
    /// map calls only — the reduce produces the reply the caller actually asked for, so a caller's
    /// formatting instruction belongs there untouched. This mirrors <c>FlareAnswerEngine</c>'s
    /// fragment protocol, which exists for the same reason: an engine's internal protocol must not
    /// be displaceable by an instruction written about the final answer.
    /// </para>
    /// </remarks>
    private const string MapProtocol =
        "You are reading ONE excerpt of several, and your reply is an intermediate result rather " +
        "than the final answer. If this text contains nothing relevant to the question, reply with " +
        "exactly: not found\n" +
        "Those two words alone — no preamble, no closing sentence, and do not apply any " +
        "end-of-reply formatting instruction to a \"not found\" reply.";

    /// <summary>
    /// Factory that constructs a <see cref="MapReduceAnswerEngine"/> by resolving all dependencies
    /// from the provided <see cref="IServiceProvider"/>. Centralizes dependency wiring so new
    /// optional dependencies added to the constructor are threaded through automatically at
    /// every registration site.
    /// </summary>
    public static MapReduceAnswerEngine CreateFromServices(IServiceProvider serviceProvider) =>
        new(
            serviceProvider.GetRequiredService<IChatClient>(),
            serviceProvider.GetService<ILogger<MapReduceAnswerEngine>>()
                ?? NullLogger<MapReduceAnswerEngine>.Instance,
            serviceProvider.GetService<IConversationMemory>(),
            serviceProvider.GetService<IContextualCompressor>());

    public async Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RagOptions();
        var mrOpts = opts.MapReduceOptions ?? new MapReduceOptions();
        var chatOptions = BuildChatOptions(opts);

        sources = await MaybeCompressAsync(sources, query, opts, cancellationToken).ConfigureAwait(false);

        var mapPrompt = mrOpts.MapPromptTemplate ?? DefaultMapPrompt;
        var reducePrompt = mrOpts.ReducePromptTemplate ?? DefaultReducePrompt;

        // Process conversation history once, then reuse across map/reduce calls
        var processedHistory = await ProcessHistoryAsync(opts, cancellationToken).ConfigureAwait(false);

        // Map step — parallel, bounded by MapConcurrency (clamped to at least 1)
        var concurrency = Math.Max(1, mrOpts.MapConcurrency);
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var mapTasks = sources.Select(source => MapOneAsync(source, query, mapPrompt, chatOptions, opts, processedHistory, semaphore, cancellationToken));
        var mapResults = await Task.WhenAll(mapTasks).ConfigureAwait(false);

        var partials = mapResults
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r) &&
                        !r.Trim().Equals("not found", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Reduce step
        var reduceText = reducePrompt
            .Replace("{partials}", string.Join("\n\n---\n\n", partials!))
            .Replace("{query}", query);

        // The reduce produces the reply the caller asked for, so their system prompt applies here as
        // written — unlike the maps, whose protocol must survive it (see MapProtocol).
        var reduceMessages = BuildMessages(reduceText, opts.SystemPrompt, processedHistory);
        var reduceResponse = await chatClient.GetResponseAsync(reduceMessages, chatOptions, cancellationToken).ConfigureAwait(false);

        return new RagResponse
        {
            Answer = reduceResponse.Text ?? string.Empty,
            Sources = sources,
        };
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Delegate to AskAsync so compression happens exactly once (inside AskAsync).
        // The returned response.Sources reflects the post-compression list.
        var response = await AskAsync(query, sources, options, cancellationToken).ConfigureAwait(false);
        yield return new RagStreamingUpdate { Sources = response.Sources };
        yield return new RagStreamingUpdate { TextDelta = response.Answer };
    }

    /// <summary>Applies contextual compression when configured and not skipped for this call.</summary>
    private async ValueTask<IReadOnlyList<SearchResult>> MaybeCompressAsync(
        IReadOnlyList<SearchResult> sources,
        string query,
        RagOptions opts,
        CancellationToken cancellationToken)
    {
        if (compressor is null || opts.SkipCompression)
        {
            return sources;
        }

        return await compressor.CompressAsync(sources, query, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> MapOneAsync(
        SearchResult source,
        string query,
        string mapPromptTemplate,
        ChatOptions chatOptions,
        RagOptions opts,
        IReadOnlyList<ChatMessage>? processedHistory,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            var prompt = mapPromptTemplate
                .Replace("{chunk}", source.CompressedText ?? source.Chunk.Text)
                .Replace("{query}", query);

            var messages = BuildMessages(prompt, MapSystemPrompt(opts), processedHistory);
            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
            return response.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AnswerEngineLog.MapReduceMapFailed(logger, source.Chunk.DocumentId.ToString(), ex);
            return null;
        }
        finally
        {
            if (acquired) semaphore.Release();
        }
    }

    private async Task<IReadOnlyList<ChatMessage>?> ProcessHistoryAsync(RagOptions opts, CancellationToken cancellationToken)
    {
        if (opts.ConversationHistory is not { Count: > 0 })
            return null;

        IReadOnlyList<ChatMessage> history = opts.ConversationHistory as IReadOnlyList<ChatMessage> ?? opts.ConversationHistory.ToList();

        if (memory is null)
            return history;

        return await memory.ProcessAsync(history, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What a map call answers under: the caller's system prompt with <see cref="MapProtocol"/>
    /// appended, or <see langword="null"/> when the caller supplied none.
    /// </summary>
    /// <remarks>
    /// <b>Null stays null deliberately.</b> With no caller system prompt there is nothing that can
    /// reshape the refusal sentinel — the map prompt already asks for <c>not found</c> — so adding
    /// the protocol would change the prompt, and therefore the output and any prompt-keyed cache,
    /// for every existing caller in order to fix a problem they do not have.
    /// </remarks>
    private static string? MapSystemPrompt(RagOptions opts) =>
        opts.SystemPrompt is null ? null : opts.SystemPrompt + "\n\n" + MapProtocol;

    private static List<ChatMessage> BuildMessages(string userText, string? systemPrompt, IReadOnlyList<ChatMessage>? processedHistory)
    {
        var messages = new List<ChatMessage>();
        if (systemPrompt is not null)
            messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
        if (processedHistory is { Count: > 0 })
            messages.AddRange(processedHistory);
        messages.Add(new ChatMessage(ChatRole.User, userText));
        return messages;
    }

    private static ChatOptions BuildChatOptions(RagOptions opts)
    {
        var chatOptions = new ChatOptions();
        if (opts.Temperature.HasValue)
            chatOptions.Temperature = opts.Temperature.Value;
        return chatOptions;
    }
}
