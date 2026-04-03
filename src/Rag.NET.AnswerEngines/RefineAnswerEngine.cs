using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerEngines;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Generates an initial answer from the first source chunk, then iteratively refines it
/// with each subsequent chunk. Sequential by design.
/// </summary>
public sealed class RefineAnswerEngine(IChatClient chatClient, ILogger<RefineAnswerEngine> logger, IConversationMemory? memory = null) : IAnswerEngine
{
    private const string DefaultInitialPrompt =
        "Answer this question using only the following context.\n\n" +
        "Context:\n{chunk}\n\nQuestion: {query}";

    private const string DefaultRefinePrompt =
        "Given the existing answer below and new context, refine the answer if the new\n" +
        "context adds useful information. If it adds nothing new, return the existing\n" +
        "answer unchanged.\n\n" +
        "Existing answer: {answer}\n\nNew context:\n{chunk}\n\nQuestion: {query}";

    public async Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RagOptions();
        var refineOpts = opts.RefineOptions ?? new RefineOptions();
        var chatOptions = BuildChatOptions(opts);

        var initialPrompt = refineOpts.InitialPromptTemplate ?? DefaultInitialPrompt;
        var refinePrompt = refineOpts.RefinePromptTemplate ?? DefaultRefinePrompt;

        if (sources.Count == 0)
            return new RagResponse { Answer = string.Empty, Sources = sources };

        // Process conversation history once for reuse across all calls
        var processedHistory = await ProcessHistoryAsync(opts, cancellationToken).ConfigureAwait(false);

        // Initial call on first chunk — always propagates on failure
        var firstChunk = sources[0];
        var initialText = initialPrompt
            .Replace("{chunk}", firstChunk.Chunk.Text)
            .Replace("{query}", query);

        var initialMessages = BuildMessages(initialText, opts, processedHistory);
        var initialResponse = await chatClient.GetResponseAsync(initialMessages, chatOptions, cancellationToken).ConfigureAwait(false);
        var currentAnswer = initialResponse.Text ?? string.Empty;

        // Refine with remaining chunks sequentially
        for (var i = 1; i < sources.Count; i++)
        {
            var source = sources[i];
            try
            {
                var refineText = refinePrompt
                    .Replace("{answer}", currentAnswer)
                    .Replace("{chunk}", source.Chunk.Text)
                    .Replace("{query}", query);

                var refineMessages = BuildMessages(refineText, opts, processedHistory);
                var refineResponse = await chatClient.GetResponseAsync(refineMessages, chatOptions, cancellationToken).ConfigureAwait(false);
                currentAnswer = refineResponse.Text ?? currentAnswer;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AnswerEngineLog.RefineStepFailed(logger, source.Chunk.DocumentId.ToString(), ex);
            }
        }

        return new RagResponse
        {
            Answer = currentAnswer,
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

    private async Task<IReadOnlyList<ChatMessage>?> ProcessHistoryAsync(RagOptions opts, CancellationToken cancellationToken)
    {
        if (opts.ConversationHistory is not { Count: > 0 })
            return null;

        IReadOnlyList<ChatMessage> history = opts.ConversationHistory as IReadOnlyList<ChatMessage> ?? opts.ConversationHistory.ToList();

        if (memory is null)
            return history;

        return await memory.ProcessAsync(history, cancellationToken).ConfigureAwait(false);
    }

    private static List<ChatMessage> BuildMessages(string userText, RagOptions opts, IReadOnlyList<ChatMessage>? processedHistory)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(opts.PromptHardeningPrefix))
            messages.Add(new ChatMessage(ChatRole.System, opts.PromptHardeningPrefix));
        if (opts.SystemPrompt is not null)
            messages.Add(new ChatMessage(ChatRole.System, opts.SystemPrompt));
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
