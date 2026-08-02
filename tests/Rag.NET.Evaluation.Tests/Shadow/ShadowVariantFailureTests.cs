using Rag.NET.Evaluation.Shadow;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// The failure half of a captured pair is strings rather than an exception object, because a
/// capture exists to be persisted and an <see cref="Exception"/> does not survive a store.
/// </summary>
public sealed class ShadowVariantFailureTests
{
    [Fact]
    public void FromException_KeepsTheTypeNameAndTheMessage()
    {
        var failure = ShadowVariantFailure.FromException(
            "shadow",
            new InvalidOperationException("the vector store went away"));

        Assert.Equal("shadow", failure.VariantName);

        // The same "{TypeName}: {Message}" line AbRunner writes into the offline report, so a
        // replayed capture reads identically to a failure the scorer caught itself.
        Assert.Equal("InvalidOperationException: the vector store went away", failure.Message);
    }

    [Fact]
    public void FromException_NullException_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => ShadowVariantFailure.FromException("shadow", null!));
}
