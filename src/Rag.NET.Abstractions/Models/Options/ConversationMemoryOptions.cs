namespace Rag.NET.Models.Options;

/// <summary>
/// Tuning for <c>IConversationMemory</c>'s trimming of conversation history before it reaches the
/// answer engine. Policies apply in a fixed order — window, then token budget, then optional
/// summarisation — each documented on its own property below.
/// </summary>
public sealed class ConversationMemoryOptions
{
    /// <summary>
    /// Maximum number of user/assistant exchange pairs to keep.
    /// Oldest exchanges removed first. System messages always preserved.
    /// Null = no window limit. Applied first.
    /// </summary>
    public int? MaxExchanges { get; init; }

    /// <summary>
    /// Maximum token budget for conversation history.
    /// Uses cl100k_base tokenizer. Oldest non-system messages trimmed
    /// until within budget. Null = no token limit. Applied second.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// When true, messages trimmed by window or token budget are
    /// LLM-summarized into a system message prefix instead of discarded.
    /// Requires IChatClient in DI. Applied last. Default false.
    /// </summary>
    public bool UseSummary { get; init; } = false;

    /// <summary>
    /// Custom prompt for the summary LLM call. Default asks for a
    /// concise summary of the conversation so far.
    /// </summary>
    public string? SummaryPromptTemplate { get; init; }
}
