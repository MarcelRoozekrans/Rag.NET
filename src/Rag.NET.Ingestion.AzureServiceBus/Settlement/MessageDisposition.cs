using System.Diagnostics.CodeAnalysis;

namespace Rag.NET.Ingestion.AzureServiceBus.Settlement;

/// <summary>
/// The decision taken about one message: which settlement to apply and, for a dead-letter,
/// the reason and description to record with it.
/// </summary>
/// <remarks>
/// Deciding and settling are kept apart deliberately. The decision is a value that can be
/// asserted on without a broker; applying it is a four-line switch over the
/// <c>ProcessMessageEventArgs</c> the SDK hands the callback.
/// </remarks>
public sealed record MessageDisposition
{
    private MessageDisposition(SettleAction action, string? reason, string? description)
    {
        Action = action;
        Reason = reason;
        Description = description;
    }

    /// <summary>The settlement to apply.</summary>
    public SettleAction Action { get; }

    /// <summary>
    /// The dead-letter reason (one of <see cref="DeadLetterReasons"/>), or
    /// <see langword="null"/> for any other action.
    /// </summary>
    public string? Reason { get; }

    /// <summary>The dead-letter description — the variable detail behind <see cref="Reason"/>.</summary>
    public string? Description { get; }

    /// <summary>Whether this disposition dead-letters the message.</summary>
    [MemberNotNullWhen(true, nameof(Reason))]
    public bool IsDeadLetter => Action == SettleAction.DeadLetter;

    /// <summary>The message succeeded and is removed from the entity.</summary>
    public static MessageDisposition Complete { get; } = new(SettleAction.Complete, null, null);

    /// <summary>The message is released for redelivery, counting a delivery attempt.</summary>
    public static MessageDisposition Abandon { get; } = new(SettleAction.Abandon, null, null);

    /// <summary>The message is left unsettled so its lock expires — host shutdown only.</summary>
    public static MessageDisposition Leave { get; } = new(SettleAction.Leave, null, null);

    /// <summary>The message moves to the dead-letter sub-queue.</summary>
    /// <param name="reason">One of <see cref="DeadLetterReasons"/>.</param>
    /// <param name="description">The variable detail behind <paramref name="reason"/>.</param>
    public static MessageDisposition DeadLetter(string reason, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(description);
        return new MessageDisposition(SettleAction.DeadLetter, reason, description);
    }
}
