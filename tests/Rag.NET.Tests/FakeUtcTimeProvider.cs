namespace Rag.NET.Tests;

/// <summary>
/// Minimal settable <see cref="TimeProvider"/> (the repo does not reference
/// Microsoft.Extensions.TimeProvider.Testing): returns a fixed UTC instant that tests
/// advance explicitly via <see cref="UtcNow"/> to cross day/month boundaries.
/// </summary>
internal sealed class FakeUtcTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
