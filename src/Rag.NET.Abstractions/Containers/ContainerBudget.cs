using System.Globalization;
using Rag.NET.Models;

namespace Rag.NET;

/// <summary>
/// The remaining nested-container allowance, shared by every <see cref="ContainerContext"/> in one
/// parse tree.
/// </summary>
/// <remarks>
/// When the parse was entered through <see cref="ContainerEntryDispatcher"/>,
/// <paramref name="sink"/> is the tag dictionary the dispatcher built for this child. Writing
/// each decrement back into it lets the parent recover the count after enumeration — without
/// that, the cap would reset for every dispatched branch and the real bound would be
/// <c>cap ^ depth</c> rather than <c>cap</c>.
/// </remarks>
/// <remarks>
/// <see cref="ContainerContext.Create"/> supplies a sink only below depth 0, so this type never
/// writes into the caller's own dictionary. That is enforced there rather than assumed here: a
/// caller is free to set a reserved tag on the metadata it passes to <c>ParseAsync</c>, and before
/// the depth test that dictionary was adopted as the sink and mutated on every
/// <see cref="Consume"/>.
/// </remarks>
public sealed class ContainerBudget(int remaining, IDictionary<string, MetadataValue>? sink)
{
    /// <summary>Nested containers still allowed before <see cref="ContainerContext.TryEnterNested"/> starts refusing them.</summary>
    public int Remaining { get; private set; } = remaining;

    /// <summary>Reserves one nested container, decrementing <see cref="Remaining"/> by one.</summary>
    public void Consume() => SetRemaining(Remaining - 1);

    /// <summary>Sets <see cref="Remaining"/> directly, writing the new value back into the sink dictionary when one was supplied.</summary>
    public void SetRemaining(int value)
    {
        Remaining = value;
        if (sink is not null)
            sink[ContainerContext.BudgetTag] = value.ToString(CultureInfo.InvariantCulture);
    }
}
