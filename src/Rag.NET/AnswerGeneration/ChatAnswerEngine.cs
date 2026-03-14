using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Generates answers by building a context prompt from search results and calling an <see cref="IChatClient"/>.
/// </summary>
public sealed class ChatAnswerEngine(IChatClient chatClient) : IAnswerEngine
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
        var (messages, chatOptions) = BuildMessages(sources, query, options ?? new RagOptions());

        var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);

        return new RagResponse
        {
            Answer = response.Text ?? string.Empty,
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

        var (messages, chatOptions) = BuildMessages(sources, query, options ?? new RagOptions());

        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            if (update.Text is not null)
            {
                yield return new RagStreamingUpdate { TextDelta = update.Text };
            }
        }
    }

    private static (List<ChatMessage> Messages, ChatOptions Options) BuildMessages(
        IReadOnlyList<SearchResult> sources,
        string query,
        RagOptions opts)
    {
        var context = string.Join("\n\n---\n\n",
            sources.Select((s, i) => $"[Source {i + 1}]\n{s.Chunk.Text}"));

        var systemPrompt = opts.SystemPrompt ?? DefaultSystemPrompt;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
        };

        if (opts.ConversationHistory is { Count: > 0 })
        {
            messages.AddRange(opts.ConversationHistory);
        }

        messages.Add(new ChatMessage(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}"));

        var chatOptions = new ChatOptions();
        if (opts.Temperature.HasValue)
        {
            chatOptions.Temperature = opts.Temperature.Value;
        }

        return (messages, chatOptions);
    }
}
