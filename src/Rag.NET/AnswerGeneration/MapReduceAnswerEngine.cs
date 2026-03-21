using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Executes one LLM call per source chunk in parallel (map), filters "not found" responses,
/// then combines surviving partials in a single reduce call.
/// </summary>
public sealed class MapReduceAnswerEngine(IChatClient chatClient, ILogger<MapReduceAnswerEngine> logger) : IAnswerEngine
{
    private const string DefaultMapPrompt =
        "Using only the following text, answer this question as best you can.\n" +
        "If the text doesn't contain relevant information, say \"not found\".\n\n" +
        "Text:\n{chunk}\n\nQuestion: {query}";

    private const string DefaultReducePrompt =
        "Synthesize the following partial answers into a single coherent response.\n" +
        "Discard redundant or contradictory information.\n\n" +
        "Partial answers:\n{partials}\n\nQuestion: {query}";

    public async Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RagOptions();
        var mrOpts = opts.MapReduceOptions ?? new MapReduceOptions();
        var chatOptions = BuildChatOptions(opts);

        var mapPrompt = mrOpts.MapPromptTemplate ?? DefaultMapPrompt;
        var reducePrompt = mrOpts.ReducePromptTemplate ?? DefaultReducePrompt;

        // Map step — parallel, bounded by MapConcurrency (clamped to at least 1)
        var concurrency = Math.Max(1, mrOpts.MapConcurrency);
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var mapTasks = sources.Select(source => MapOneAsync(source, query, mapPrompt, chatOptions, opts, semaphore, cancellationToken));
        var mapResults = await Task.WhenAll(mapTasks).ConfigureAwait(false);

        var partials = mapResults
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r) &&
                        !r.Trim().Equals("not found", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Reduce step
        var reduceText = reducePrompt
            .Replace("{partials}", string.Join("\n\n---\n\n", partials!))
            .Replace("{query}", query);

        var reduceMessages = BuildMessages(reduceText, opts);
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
        yield return new RagStreamingUpdate { Sources = sources };

        var response = await AskAsync(query, sources, options, cancellationToken).ConfigureAwait(false);
        yield return new RagStreamingUpdate { TextDelta = response.Answer };
    }

    private async Task<string?> MapOneAsync(
        SearchResult source,
        string query,
        string mapPromptTemplate,
        ChatOptions chatOptions,
        RagOptions opts,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            var prompt = mapPromptTemplate
                .Replace("{chunk}", source.Chunk.Text)
                .Replace("{query}", query);

            var messages = BuildMessages(prompt, opts);
            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
            return response.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.MapReduceMapFailed(logger, source.Chunk.DocumentId.ToString(), ex);
            return null;
        }
        finally
        {
            if (acquired) semaphore.Release();
        }
    }

    private static List<ChatMessage> BuildMessages(string userText, RagOptions opts)
    {
        var messages = new List<ChatMessage>();
        if (opts.SystemPrompt is not null)
            messages.Add(new ChatMessage(ChatRole.System, opts.SystemPrompt));
        if (opts.ConversationHistory is { Count: > 0 })
            messages.AddRange(opts.ConversationHistory);
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
