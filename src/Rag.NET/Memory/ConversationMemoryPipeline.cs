using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Memory;

/// <summary>
/// Applies sliding-window and token-budget strategies to trim conversation history.
/// Strategies are applied in order: sliding window first, then token budget, then optional summary.
/// System messages are always preserved.
/// </summary>
public sealed class ConversationMemoryPipeline : IConversationMemory
{
    private const string DefaultSummaryPrompt =
        "Summarize the following conversation concisely, preserving key facts and context:\n\n{messages}";

    private readonly ConversationMemoryOptions _options;
    private readonly IChatClient? _chatClient;
    private readonly ILogger _logger;
    private readonly Tokenizer _tokenizer;

    public ConversationMemoryPipeline(ConversationMemoryOptions options, IChatClient? chatClient, ILogger<ConversationMemoryPipeline>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _chatClient = chatClient;
        _logger = logger ?? NullLogger<ConversationMemoryPipeline>.Instance;
        _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
    }

    public async Task<IReadOnlyList<ChatMessage>> ProcessAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (history.Count == 0)
        {
            return [];
        }

        var result = new List<ChatMessage>(history);
        var allTrimmed = new List<ChatMessage>();

        // Step 1: Sliding window
        if (_options.MaxExchanges.HasValue)
        {
            var (kept, trimmed) = ApplySlidingWindow(result, _options.MaxExchanges.Value);
            allTrimmed.AddRange(trimmed);
            result = kept;
        }

        // Step 2: Token budget
        if (_options.MaxTokens.HasValue)
        {
            var (kept, trimmed) = ApplyTokenBudget(result, _options.MaxTokens.Value);
            allTrimmed.AddRange(trimmed);
            result = kept;
        }

        // Step 3: Summary
        if (_options.UseSummary && allTrimmed.Count > 0 && _chatClient is not null)
        {
            var summary = await GenerateSummaryAsync(allTrimmed, cancellationToken).ConfigureAwait(false);
            if (summary is not null)
            {
                result.Insert(0, new ChatMessage(ChatRole.System, $"Summary of earlier conversation: {summary}"));
            }
        }

        return result;
    }

    private async Task<string?> GenerateSummaryAsync(
        List<ChatMessage> trimmedMessages, CancellationToken cancellationToken)
    {
        try
        {
            var formatted = string.Join("\n", trimmedMessages.Select(m => $"{m.Role}: {m.Text}"));
            var template = _options.SummaryPromptTemplate ?? DefaultSummaryPrompt;
            var prompt = template.Replace("{messages}", formatted);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, prompt),
            };

            var response = await _chatClient!.GetResponseAsync(messages, options: null, cancellationToken).ConfigureAwait(false);
            return response.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.ConversationSummaryFailed(_logger, ex);
            return null;
        }
    }

    private static (List<ChatMessage> Kept, List<ChatMessage> Trimmed) ApplySlidingWindow(
        List<ChatMessage> messages, int maxExchanges)
    {
        var systemMessages = messages.Where(m => m.Role == ChatRole.System).ToList();
        var nonSystemMessages = messages.Where(m => m.Role != ChatRole.System).ToList();

        int keepCount = maxExchanges * 2;

        if (nonSystemMessages.Count <= keepCount)
        {
            return (messages, []);
        }

        var trimmed = nonSystemMessages.Take(nonSystemMessages.Count - keepCount).ToList();
        var kept = nonSystemMessages.Skip(nonSystemMessages.Count - keepCount).ToList();

        var result = new List<ChatMessage>(systemMessages.Count + kept.Count);
        result.AddRange(systemMessages);
        result.AddRange(kept);

        return (result, trimmed);
    }

    private (List<ChatMessage> Kept, List<ChatMessage> Trimmed) ApplyTokenBudget(
        List<ChatMessage> messages, int maxTokens)
    {
        int totalTokens = messages.Sum(m => CountTokens(m));

        if (totalTokens <= maxTokens)
        {
            return (messages, []);
        }

        var result = new List<ChatMessage>(messages);
        var trimmed = new List<ChatMessage>();

        // Remove oldest non-system messages one by one until within budget
        while (totalTokens > maxTokens)
        {
            int idx = result.FindIndex(m => m.Role != ChatRole.System);
            if (idx < 0)
            {
                break; // only system messages left
            }

            totalTokens -= CountTokens(result[idx]);
            trimmed.Add(result[idx]);
            result.RemoveAt(idx);
        }

        return (result, trimmed);
    }

    public Task StoreAsync(
        string userMessage,
        string assistantMessage,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private int CountTokens(ChatMessage message)
    {
        var text = message.Text ?? string.Empty;
        return _tokenizer.CountTokens(text);
    }
}
