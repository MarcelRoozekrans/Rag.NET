using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins the three things <see cref="BeirProtocolSet"/> exists to provide that a BCL set could not:
/// value equality, a legible rendering, and a bitmask that cannot silently alias one protocol onto
/// another.
/// </summary>
/// <remarks>
/// Equality against a real <see cref="BeirDatasetDescriptor"/> is asserted in
/// <c>BeirDatasetDescriptorTests</c>, which is where the defect actually lived; what is pinned here
/// is the value itself, including the case the descriptor cannot reach — a mask wide enough for
/// every protocol the enum declares.
/// </remarks>
public sealed class BeirProtocolSetTests
{
    [Fact]
    public void TwoSetsBuiltSeparatelyFromTheSameProtocols_AreEqual()
    {
        // Built in opposite orders and with a repeat, because a set has neither.
        var a = BeirProtocolSet.Of(BeirProtocol.Parity, BeirProtocol.GraphRag, BeirProtocol.Parity);
        var b = BeirProtocolSet.Of(BeirProtocol.GraphRag, BeirProtocol.Parity);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(2, a.Count);
    }

    [Fact]
    public void EveryProtocolTheEnumDeclares_FitsInTheMask()
    {
        // The tripwire for the storage, and the reason Capacity is a constant rather than a comment.
        // 1u << 32 does not overflow in C#; the shift count is masked to five bits, so it wraps. A
        // thirty-third protocol would therefore alias the first, and a descriptor restricted to the
        // new one would report itself measurable under Parity — a wrong answer that runs, which is
        // this harness's most expensive kind. Adding protocols is fine; adding the thirty-third
        // means widening the mask to ulong first.
        var protocols = Enum.GetValues<BeirProtocol>();

        Assert.All(protocols, protocol => Assert.True(
            (int)protocol < BeirProtocolSet.Capacity,
            $"{protocol} has ordinal {(int)protocol}, which a {BeirProtocolSet.Capacity}-bit mask " +
            "cannot hold; widen BeirProtocolSet's mask before declaring it."));

        // And the whole enum in one set really is the whole enum, rather than a wrapped subset.
        Assert.Equal(protocols.Length, BeirProtocolSet.Of(protocols).Count);
    }

    [Fact]
    public void AValueTheEnumDoesNotDeclare_IsRefusedRatherThanGivenSomebodyElsesBit()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => BeirProtocolSet.Of((BeirProtocol)99));

        Assert.Contains("BeirProtocol", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_NamesTheProtocols()
    {
        // The record struct's own synthesis prints public fields and properties, which here would
        // render every set as "BeirProtocolSet { Count = 2 }" — and the FrozenSet this replaced
        // rendered as its own type name on both sides of a failed diff. A set of protocols that
        // cannot say which protocols is not much use in an assertion.
        Assert.Equal(
            "BeirProtocolSet { Parity, GraphRag }",
            BeirProtocolSet.Of(BeirProtocol.GraphRag, BeirProtocol.Parity).ToString(),
            StringComparer.Ordinal);

        Assert.Equal("BeirProtocolSet { }", BeirProtocolSet.Of().ToString(), StringComparer.Ordinal);
    }
}
