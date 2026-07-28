using Microsoft.Extensions.AI;

namespace Rag.NET.Abstractions;

/// <summary>
/// Optional sink an answer engine hands the assembled prompt to, once, just before sending it.
/// </summary>
/// <remarks>
/// <para>
/// <i>"What the answer engine was actually given"</i> is the one thing about a query that nothing
/// else can see. The engine builds it from the system prompt, the conversation history and the
/// retrieved chunks and then sends it, and no behavior, decorator or span sits between those two
/// steps. This is the seam that does.
/// </para>
/// <para>
/// <b>Registering an implementation is the only thing that turns it on.</b> With none registered the
/// engine holds <see langword="null"/> and never calls anything, so answer generation is unchanged
/// down to the allocation. A debugger that altered generation in order to observe it would have
/// failed at its only job.
/// </para>
/// <para>
/// <b>Implementations must never throw</b>, the same contract <see cref="IRetrievalGuard"/> and
/// <see cref="IChunkSanitiser"/> carry. The engine calls this on the path to the model; an observer
/// that threw would turn a diagnostic into a failed answer.
/// </para>
/// <para>
/// The messages are passed as assembled rather than flattened into a string. The engine has no
/// business choosing how a prompt is rendered for something it knows nothing about, and a rendering
/// cannot be undone by the observer afterwards.
/// </para>
/// </remarks>
public interface IPromptObserver
{
    /// <summary>Called once per answer, with the messages about to be sent.</summary>
    /// <param name="messages">
    /// The complete prompt: system messages, history and the context-plus-question turn, in the
    /// order the model will receive them. Treat it as read-only — it is the engine's own list.
    /// </param>
    void OnPromptAssembled(IReadOnlyList<ChatMessage> messages);
}
