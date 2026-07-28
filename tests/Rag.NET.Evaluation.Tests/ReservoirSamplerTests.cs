using Rag.NET.Evaluation.Internal;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

/// <summary>
/// The sampler is a pure function over a source and a <see cref="Random"/>, so it can be pinned
/// exhaustively with no LLM and no data manager — which is why it lands before the builder change
/// that consumes it.
/// </summary>
public sealed class ReservoirSamplerTests
{
    [Fact]
    public void Sample_SameSeed_SelectsTheSameItems()
    {
        var source = Enumerable.Range(0, 100).Select(i => $"item {i}").ToList();

        var first = ReservoirSampler.Sample(source, 10, new Random(1234));
        var second = ReservoirSampler.Sample(source, 10, new Random(1234));

        // The guarantee the phase exists to add: a dataset can be regenerated.
        Assert.Equal(first, second);
    }

    [Fact]
    public void Sample_DifferentSeeds_GenerallySelectDifferentItems()
    {
        var source = Enumerable.Range(0, 100).Select(i => $"item {i}").ToList();

        var a = ReservoirSampler.Sample(source, 10, new Random(1));
        var b = ReservoirSampler.Sample(source, 10, new Random(2));

        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(100, 10)]     // k larger than the source clamps to what exists
    public void Sample_ReturnsAtMostWhatExists(int k, int expected)
        => Assert.Equal(expected, ReservoirSampler.Sample(Enumerable.Range(0, 10).Select(i => $"{i}").ToList(), k, new Random(1)).Count);

    [Fact]
    public void Sample_SelectsEveryItemWithRoughlyEqualFrequency()
    {
        // Uniformity is the property that makes the dataset representative. Loose bounds:
        // this is a sanity check against a badly skewed reservoir, not a statistical proof. It
        // would not detect a subtle bias, and nobody should later cite it as evidence of one's
        // absence — what it does catch is a reservoir that never evicts, or that evicts only from
        // one end, which is the way Algorithm R is usually got wrong.
        var source = Enumerable.Range(0, 10).Select(i => i).ToList();
        var counts = new int[10];
        for (var seed = 0; seed < 2000; seed++)
        {
            foreach (var picked in ReservoirSampler.Sample(source, 3, new Random(seed)))
                counts[picked]++;
        }

        Assert.All(counts, c => Assert.InRange(c, 400, 800));   // expected 600 each
    }

    [Fact]
    public void Offer_NeverHoldsMoreThanCapacity()
    {
        // The bound the streaming builder relies on: the reservoir is proportional to the sample,
        // not to the corpus. That is the reservoir's own bound, not the whole build's — the builder
        // still materialises the document list and one document's chunks at a time, because
        // IRagDataManager has no streaming overload. Asserted against the reservoir's own size
        // rather than a memory measurement, which would be measuring the GC rather than the
        // algorithm.
        var reservoir = new ReservoirSampler<int>(5, new Random(7));

        for (var i = 0; i < 10_000; i++)
        {
            reservoir.Offer(i);
            Assert.True(reservoir.Count <= 5, $"reservoir grew to {reservoir.Count} after {i + 1} items");
        }

        Assert.Equal(5, reservoir.Count);
    }

    [Fact]
    public void Offer_ItemByItem_MatchesTheWholeSourceOverload()
    {
        // The builder drives Offer from its own loop; it must select exactly what the eager
        // overload would, or the seed would mean something different in the two paths.
        var source = Enumerable.Range(0, 500).Select(i => $"item {i}").ToList();

        var eager = ReservoirSampler.Sample(source, 12, new Random(99));

        var streamed = new ReservoirSampler<string>(12, new Random(99));
        foreach (var item in source)
            streamed.Offer(item);

        Assert.Equal(eager, streamed.ToList());
    }

    [Fact]
    public void Offer_WithZeroCapacity_KeepsNothing()
    {
        var reservoir = new ReservoirSampler<int>(0, new Random(1));

        for (var i = 0; i < 100; i++)
            reservoir.Offer(i);

        Assert.Empty(reservoir.ToList());
    }

    [Fact]
    public void Constructor_WithNegativeCapacity_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ReservoirSampler<int>(-1, new Random(1)));

    [Fact]
    public void Constructor_WithAnEnormousCapacity_DoesNotAllocateItUpFront()
    {
        // `new List<T>(capacity)` allocates a T[capacity] immediately, so an unclamped capacity
        // turned SampleCount = int.MaxValue into an OutOfMemoryException before a single chunk had
        // been read. The pre-Phase-3.2 builder clamped the count against the corpus first and
        // simply returned every chunk, so this was a regression introduced by the streaming
        // rewrite, not a pre-existing limit.
        var reservoir = new ReservoirSampler<string>(int.MaxValue, new Random(1));

        for (var i = 0; i < 100; i++)
            reservoir.Offer($"item {i}");

        // The capacity is still honoured as a ceiling — it is only the up-front allocation that is
        // clamped — so a stream shorter than it is kept whole.
        Assert.Equal(100, reservoir.Count);
    }

    [Fact]
    public void Sample_WithANegativeCount_NamesTheCallersParameter()
    {
        // The constructor's own guard reports ParamName "capacity", which is the callee's name for
        // it and not a parameter this caller passed.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => ReservoirSampler.Sample<string>([], -1, new Random(1)));

        Assert.Equal("count", exception.ParamName);
    }
}
