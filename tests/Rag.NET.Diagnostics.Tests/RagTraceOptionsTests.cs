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
    public void EveryProperty_IsAssignableThroughAConfigureDelegate()
    {
        var options = new RagTraceOptions();

        // Registration configures these the way AddAuditLog does — AddRagDiagnostics(o => ...) —
        // and a delegate is handed an instance that already exists. init-only accessors would not
        // compile here, so this test guards a decision the compiler otherwise enforces silently.
        Action<RagTraceOptions> configure = o =>
        {
            o.Capacity = 5;
            o.MaxCapturedCharacters = 100;
            o.CaptureQueryText = true;
            o.CaptureChunkText = true;
            o.CapturePromptText = true;
            o.CaptureAnswerText = true;
        };

        configure(options);

        Assert.Equal(5, options.Capacity);
        Assert.Equal(100, options.MaxCapturedCharacters);
        Assert.True(options.CaptureQueryText);
        Assert.True(options.CaptureChunkText);
        Assert.True(options.CapturePromptText);
        Assert.True(options.CaptureAnswerText);
    }

    [Fact]
    public void Validation_StillBitesWhenSetAfterConstruction()
    {
        var options = new RagTraceOptions();

        // The accessor changed from init to set; the refusal it guards did not.
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Capacity = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxCapturedCharacters = -1);
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
