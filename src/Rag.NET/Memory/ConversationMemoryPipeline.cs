using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.Memory;

/// <summary>
/// Applies sliding-window and token-budget strategies to trim conversation history.
/// Strategies are applied in order: sliding window first, then token budget.
/// System messages are always preserved.
/// </summary>
public sealed class ConversationMemoryPipeline : IConversationMemory
{
    private readonly ConversationMemoryOptions _options;
    private readonly IChatClient? _chatClient;
    private readonly Tokenizer _tokenizer;

    /// <summary>
    /// Gets the messages that were trimmed during the last <see cref="ProcessAsync"/> call.
    /// Useful for downstream summarization.
    /// </summary>
    public IReadOnlyList<ChatMessage> TrimmedMessages { get; private set; } = [];

    public ConversationMemoryPipeline(ConversationMemoryOptions options, IChatClient? chatClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _chatClient = chatClient;
        _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");
    }

    public Task<IReadOnlyList<ChatMessage>> ProcessAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        if (history.Count == 0)
        {
            TrimmedMessages = [];
            return Task.FromResult<IReadOnlyList<ChatMessage>>([]);
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

        TrimmedMessages = allTrimmed;
        return Task.FromResult<IReadOnlyList<ChatMessage>>(result);
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

    private int CountTokens(ChatMessage message)
    {
        var text = message.Text ?? string.Empty;
        return _tokenizer.CountTokens(text);
    }
}
