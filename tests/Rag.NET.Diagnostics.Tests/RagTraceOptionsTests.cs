using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// The defaults are the phase's safety story, so they are asserted rather than assumed: registering
/// diagnostics captures structure, and every text field takes a further explicit flag.
/// </summary>
public sealed class RagTraceOptionsTests
{
    [Fact]
    public void Defaults_CaptureNoTextAtAll()
    {
        var options = new RagTraceOptions();

        // "Turn on debugging" must not silently mean "start retaining customer documents and user
        // questions in process memory".
        Assert.False(options.CaptureQueryText);
        Assert.False(options.CaptureChunkText);
        Assert.False(options.CapturePromptText);
        Assert.False(options.CaptureAnswerText);
    }

    [Fact]
    public void Defaults_AreTheBoundsTheDesignSettledOn()
    {
        var options = new RagTraceOptions();

        Assert.Equal(50, options.Capacity);
        Assert.Equal(4000, options.MaxCapturedCharacters);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Capacity_BelowOne_IsRefused(int capacity)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RagTraceOptions { Capacity = capacity });

        // MA0015 forces paramName to a real parameter, and an init accessor's is "value" — so the
        // property has to be named in the message for the throw to point anywhere useful.
        Assert.Contains(nameof(RagTraceOptions.Capacity), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Capacity_OfOne_IsAllowed()
        => Assert.Equal(1, new RagTraceOptions { Capacity = 1 }.Capacity);

    [Fact]
    public void MaxCapturedCharacters_Negative_IsRefused()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RagTraceOptions { MaxCapturedCharacters = -1 });

        Assert.Contains(
            nameof(RagTraceOptions.MaxCapturedCharacters),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MaxCapturedCharacters_OfZero_IsAllowed()
    {
        // Flags on, nothing retained: a way to confirm the wiring without keeping any content.
        Assert.Equal(0, new RagTraceOptions { MaxCapturedCharacters = 0 }.MaxCapturedCharacters);
    }
}
