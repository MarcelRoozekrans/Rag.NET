using System.Globalization;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// The remaining embedded-message allowance, shared by every
/// <see cref="EmbeddedMessageContext"/> in one parse tree.
/// </summary>
/// <remarks>
/// When the parse was entered through <see cref="EmailAttachmentDispatcher"/>,
/// <paramref name="sink"/> is the tag dictionary the dispatcher built for this child. Writing
/// each decrement back into it lets the parent recover the count after enumeration — without
/// that, the cap would reset for every dispatched branch and the real bound would be
/// <c>cap ^ depth</c> rather than <c>cap</c>. It is never the caller's own dictionary: at the
/// top level <paramref name="sink"/> is <see langword="null"/>.
/// </remarks>
internal sealed class EmbeddedMessageBudget(int remaining, IDictionary<string, string>? sink)
{
    public int Remaining { get; private set; } = remaining;

    public void Consume() => SetRemaining(Remaining - 1);

    public void SetRemaining(int value)
    {
        Remaining = value;
        if (sink is not null)
            sink[EmbeddedMessageContext.BudgetTag] = value.ToString(CultureInfo.InvariantCulture);
    }
}
