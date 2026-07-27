namespace Rag.NET.Ingestion.AzureServiceBus.Settlement;

/// <summary>How the trigger settles a message with the broker.</summary>
public enum SettleAction
{
    /// <summary>Leave the message unsettled — the lock expires and the broker redelivers it.</summary>
    /// <remarks>
    /// Reserved for host shutdown. Abandoning would also redeliver, but it counts a delivery
    /// attempt against a message that never actually got one.
    /// </remarks>
    Leave,

    /// <summary><c>CompleteMessageAsync</c> — the message is removed from the entity.</summary>
    Complete,

    /// <summary>
    /// <c>AbandonMessageAsync</c> — the lock is released immediately, the broker redelivers,
    /// and the delivery count increments.
    /// </summary>
    Abandon,

    /// <summary>
    /// <c>DeadLetterMessageAsync</c> with a reason — the message moves to the dead-letter
    /// sub-queue where an operator can inspect it.
    /// </summary>
    DeadLetter,
}
