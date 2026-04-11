using Microsoft.Extensions.AI;
using Rag.NET.Models;

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

    /// <summary>
    /// Persists a completed exchange pair for future recall.
    /// Implementations that do not support persistence return <see cref="Task.CompletedTask"/>.
    /// </summary>
    /// <param name="userMessage">The user turn text to persist.</param>
    /// <param name="assistantMessage">The assistant response text to persist.</param>
    /// <param name="sessionId">
    /// Scoping key for the exchange. Used by persistent implementations to
    /// namespace stored vectors (e.g., per-user or per-conversation).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StoreAsync(
        string userMessage,
        string assistantMessage,
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}
