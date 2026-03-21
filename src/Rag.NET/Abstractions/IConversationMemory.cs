using Microsoft.Extensions.AI;

namespace Rag.NET.Abstractions;

/// <summary>
/// Processes conversation history to fit within configured memory constraints.
/// </summary>
public interface IConversationMemory
{
    /// <summary>
    /// Returns a trimmed or summarized copy of <paramref name="history"/>
    /// according to configured window, token, and summary policies.
    /// </summary>
    Task<IReadOnlyList<ChatMessage>> ProcessAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default);
}
