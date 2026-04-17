using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Generates answers by building a context prompt from search results and calling an <see cref="IChatClient"/>.
/// </summary>
public sealed class ChatAnswerEngine(IChatClient chatClient, IConversationMemory? memory = null) : IAnswerEngine
{
    private const string DefaultSystemPrompt =
        "Answer the user's question based only on the provided context. " +
        "If the context doesn't contain enough information, say so. " +
        "Cite which sources you used.";

    public async Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RagOptions();

        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.ask");
        activity?.SetTag("source.count", sources.Count);
        activity?.SetTag("synthesis.strategy", opts.SynthesisStrategy.ToString());

        var (messages, chatOptions) = await BuildMessagesAsync(sources, query, opts, cancellationToken).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
            return new RagResponse
            {
                Answer = response.Text ?? string.Empty,
                Sources = sources,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            sw.Stop();
            RagTelemetry.AskDuration.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RagOptions();

        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.ask");
        activity?.SetTag("source.count", sources.Count);
        activity?.SetTag("synthesis.strategy", opts.SynthesisStrategy.ToString());

        yield return new RagStreamingUpdate { Sources = sources };

        var (messages, chatOptions) = await BuildMessagesAsync(sources, query, opts, cancellationToken).ConfigureAwait(false);

        // C# async iterators cannot yield inside a try/catch, so we drive the enumerator manually:
        // MoveNextAsync() and Current are accessed outside yield, keeping yield clean.
        var enumerator = chatClient
            .GetStreamingResponseAsync(messages, chatOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        var sw = Stopwatch.StartNew();
        bool hasNext;
        try
        {
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }

            while (hasNext)
            {
                var update = enumerator.Current;
                if (update.Text is not null)
                {
                    yield return new RagStreamingUpdate { TextDelta = update.Text };
                }

                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    throw;
                }
            }
        }
        finally
        {
            sw.Stop();
            RagTelemetry.AskDuration.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<(List<ChatMessage> Messages, ChatOptions Options)> BuildMessagesAsync(
        IReadOnlyList<SearchResult> sources,
        string query,
        RagOptions opts,
        CancellationToken cancellationToken)
    {
        var context = string.Join("\n\n---\n\n",
            sources.Select((s, i) => $"[Source {i + 1}]\n{s.Chunk.Text}"));

        var systemPrompt = opts.SystemPrompt ?? DefaultSystemPrompt;

        var messages = new List<ChatMessage>();

        IReadOnlyList<ChatMessage>? history = null;
        if (opts.ConversationHistory is { Count: > 0 })
        {
            history = opts.ConversationHistory as IReadOnlyList<ChatMessage> ?? opts.ConversationHistory.ToList();
            if (memory is not null)
                history = await memory.ProcessAsync(history, cancellationToken).ConfigureAwait(false);
        }

        // Leading system messages from history (e.g. a prompt-hardening prefix) go FIRST
        // so they are not shadowed by the primary system prompt below.
        var historyStart = 0;
        if (history is not null)
        {
            while (historyStart < history.Count && history[historyStart].Role == ChatRole.System)
            {
                messages.Add(history[historyStart]);
                historyStart++;
            }
        }

        messages.Add(new ChatMessage(ChatRole.System, systemPrompt));

        // Remaining history — user/assistant turns
        if (history is not null)
            for (var i = historyStart; i < history.Count; i++)
                messages.Add(history[i]);

        messages.Add(new ChatMessage(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}"));

        var chatOptions = new ChatOptions();
        if (opts.Temperature.HasValue)
        {
            chatOptions.Temperature = opts.Temperature.Value;
        }

        return (messages, chatOptions);
    }
}
