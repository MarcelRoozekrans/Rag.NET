using System.Globalization;
using System.Numerics;
using System.Text;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// A set of <see cref="BeirProtocol"/>s, held as a bitmask and compared <b>by value</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because no BCL set compares by value.</b> Not <see cref="HashSet{T}"/>, not
/// <c>FrozenSet&lt;T&gt;</c>, not <c>ImmutableHashSet&lt;T&gt;</c> — none of them override
/// <see cref="object.Equals(object)"/>, so <c>EqualityComparer&lt;T&gt;.Default</c> falls through to
/// reference equality. <see cref="BeirDatasetDescriptor"/> is a <see langword="record"/>, and a
/// record's synthesized <c>Equals</c> and <c>GetHashCode</c> are built from exactly that comparer,
/// so two descriptors identical in every field including protocol content compared unequal and
/// hashed differently. Verified empirically before this type existed: the failing assertion printed
/// the two descriptors character-for-character identically and still called them different.
/// </para>
/// <para>
/// A bitmask is what makes the fix free rather than careful. <see cref="Nullable{T}"/> forwards
/// equality to the underlying value, so <c>BeirProtocolSet?</c> gets value semantics from the
/// language and there is no comparer to remember to pass and no <c>Equals</c> override to keep in
/// step with a <c>GetHashCode</c> override.
/// </para>
/// <para>
/// <b>And it is genuinely immutable, not a read-only view.</b> That distinction is the whole reason
/// <see cref="BeirDatasetDescriptor.ApplicableProtocols"/> used to copy what it was handed:
/// <see cref="IReadOnlySet{T}"/> is a window onto a set somebody else can still mutate, and the
/// descriptors are <see langword="static"/> fields, so a caller keeping its
/// <see cref="HashSet{T}"/> could change what the harness measures process-wide, at a moment
/// decided by whichever test ran first. A <see langword="struct"/> over a <see cref="uint"/> has no
/// referent to reach back through, so the invariant is enforced by the type rather than by
/// remembering to copy.
/// </para>
/// <para>
/// The last thing it buys is legibility. A failed assertion over a set of protocols used to print
/// <c>System.Collections.Frozen.SmallValueTypeComparableFrozenSet`1[BeirProtocol]</c> on both sides
/// of the diff; <see cref="ToString"/> here names the protocols.
/// </para>
/// </remarks>
public readonly record struct BeirProtocolSet
{
    /// <summary>
    /// How many protocols a mask can hold. <see cref="BeirProtocol"/> declares twelve.
    /// </summary>
    /// <remarks>
    /// Checked rather than assumed, because the failure would be silent and specific:
    /// <c>1u &lt;&lt; 32</c> does not overflow in C#, it wraps — the shift count is masked to five
    /// bits — so a thirty-third protocol would quietly alias the first and a descriptor restricted
    /// to the new one would report itself measurable under <see cref="BeirProtocol.Parity"/>.
    /// <c>BeirProtocolSetTests</c> asserts the enum still fits.
    /// </remarks>
    public const int Capacity = 32;

    private readonly uint _mask;

    private BeirProtocolSet(uint mask) => _mask = mask;

    /// <summary>Gets how many protocols are in the set.</summary>
    public int Count => BitOperations.PopCount(_mask);

    /// <summary>
    /// Builds a set from the protocols named.
    /// </summary>
    /// <param name="protocols">
    /// The protocols. Repeats are absorbed and order is irrelevant, as they are for any set — which
    /// is what makes two separately written declarations of the same protocols compare equal.
    /// </param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is not a declared <see cref="BeirProtocol"/>, or is declared with an ordinal at or
    /// beyond <see cref="Capacity"/>.
    /// </exception>
    /// <remarks>
    /// An empty result is allowed here and refused where it would matter, by
    /// <see cref="BeirDatasetDescriptor.ApplicableProtocols"/> — a set is a value and the empty
    /// value is a perfectly good one; a descriptor measurable under no protocol is not. Keeping the
    /// refusal in one place keeps its explanation in one place too.
    /// </remarks>
    public static BeirProtocolSet Of(params ReadOnlySpan<BeirProtocol> protocols)
    {
        var mask = 0u;

        foreach (ref readonly var protocol in protocols)
        {
            if (!Enum.IsDefined(protocol) || (uint)protocol >= Capacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(protocols),
                    protocol,
                    "Not a protocol this set can hold. A value outside BeirProtocol's declared " +
                    "members, or one whose ordinal reaches " +
                    Capacity.ToString(CultureInfo.InvariantCulture) +
                    ", cannot be given a bit: the shift would wrap and silently alias another " +
                    "protocol rather than fail.");
            }

            mask |= 1u << (int)protocol;
        }

        return new BeirProtocolSet(mask);
    }

    /// <summary>Reports whether one protocol is in the set.</summary>
    /// <param name="protocol">The protocol to ask about.</param>
    /// <returns><see langword="true"/> when the set contains it.</returns>
    /// <remarks>
    /// An undefined value answers <see langword="false"/> rather than throwing: nothing can have put
    /// it in, since <see cref="Of"/> refuses it. The mask is taken modulo
    /// <see cref="Capacity"/> by the shift itself, so this cannot read out of bounds.
    /// </remarks>
    public bool Contains(BeirProtocol protocol) =>
        (uint)protocol < Capacity && (_mask & (1u << (int)protocol)) != 0;

    /// <summary>
    /// The protocols by name, in <see cref="BeirProtocol"/>'s declaration order.
    /// </summary>
    /// <returns>Something a failed assertion can be read from.</returns>
    /// <remarks>
    /// Overridden rather than left to the record struct's synthesis, which prints only public
    /// fields and properties and would therefore render every set as <c>BeirProtocolSet { Count = 2 }</c>.
    /// </remarks>
    public override string ToString()
    {
        var builder = new StringBuilder("BeirProtocolSet { ");
        var written = 0;

        for (var ordinal = 0; ordinal < Capacity; ordinal++)
        {
            if ((_mask & (1u << ordinal)) == 0)
            {
                continue;
            }

            _ = builder
                .Append(written == 0 ? string.Empty : ", ")
                .Append(((BeirProtocol)ordinal).ToString());

            written++;
        }

        return builder.Append(written == 0 ? "}" : " }").ToString();
    }
}
