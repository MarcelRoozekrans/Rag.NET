using System.Globalization;
using Rag.NET.Models;

namespace Rag.NET;

/// <summary>
/// The decompressed bytes a document has already cost, shared by every
/// <see cref="ContainerContext"/> in one parse tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this crosses the <c>IDocumentParser</c> boundary at all.</b> A byte bound that a container
/// parser applied per invocation would be no bound on a document: a nested archive re-enters through
/// <see cref="ContainerEntryDispatcher"/> and would get a fresh allowance, costing its parent only its
/// small compressed size. With the nesting budget's default of 50 that turns a configured cap into
/// roughly <c>51 × cap</c> — the same shape of hole <see cref="ContainerBudget"/> documents one level
/// down, reproduced one level up. So the running total rides the reserved tags for the same reason
/// depth and the nesting budget do.
/// </para>
/// <para>
/// <b>Carried as bytes already spent, not as an allowance remaining, and that is what makes an
/// attacker-supplied value safe.</b> <c>DocumentMetadata.Tags</c> comes from the caller and a
/// connector can populate it from remote data. A larger <see cref="Spent"/> is more restrictive, so it
/// is taken as read; there is no value of it that buys more work, because the only direction that
/// would is downwards and <see cref="ContainerContext.Create"/>'s fallback is already the smallest
/// value there is. A remaining-allowance encoding would have needed a clamp against a cap this
/// format-neutral type does not know, and would have had to express overspend as a negative number.
/// </para>
/// <para>
/// <see cref="ContainerContext.Create"/> supplies a <paramref name="sink"/> only below depth 0, on the
/// same terms as <see cref="ContainerBudget"/>: the dictionary at depth 0 belongs to the caller and
/// reaches stored chunk metadata. Writing each addition back into the dictionary the dispatcher built
/// for a child is what lets a parent recover the count after the child's enumeration — including when
/// the child refused itself and the dispatcher contained the refusal.
/// </para>
/// <para>
/// The sink is written on every addition, which is once per read of an archive entry. That is a
/// dictionary write and a small string per read, against the decompression that produced the bytes
/// being counted; flushing per entry instead would be faster and would depend on every exit path
/// remembering to flush.
/// </para>
/// </remarks>
/// <param name="spent">The bytes already spent when this context was entered.</param>
/// <param name="sink">The child tag dictionary to write each addition back into, or <see langword="null"/>.</param>
public sealed class ContainerByteBudget(long spent, IDictionary<string, MetadataValue>? sink)
{
    /// <summary>Gets the decompressed bytes this document has cost so far, across every container.</summary>
    public long Spent { get; private set; } = spent;

    /// <summary>Books bytes that have <b>already been read</b> against the document's running total.</summary>
    /// <param name="count">The byte count a read returned.</param>
    public void Add(long count) => SetSpent(Spent + count);

    /// <summary>
    /// Adopts a total, refusing to move it backwards. Bytes already read cannot become unread, so a
    /// lower value is either a child that never counted anything or a tag someone edited; both are
    /// answered by keeping the larger number.
    /// </summary>
    /// <param name="value">The candidate total.</param>
    public void SetSpent(long value)
    {
        if (value <= Spent)
            return;

        Spent = value;
        if (sink is not null)
            sink[ContainerContext.BytesTag] = value.ToString(CultureInfo.InvariantCulture);
    }
}
