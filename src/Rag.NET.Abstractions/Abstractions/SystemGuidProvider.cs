namespace Rag.NET.Abstractions;

/// <summary>The real <see cref="IGuidProvider"/>, delegating to <see cref="Guid.NewGuid"/>.</summary>
/// <remarks>
/// <see cref="Instance"/> exists so nothing has to be registered for production behaviour: every
/// component takes an optional <see cref="IGuidProvider"/> and falls back to this, the way
/// <c>TimeProvider.System</c> is used elsewhere in the library. Registering one is what a test
/// does, not what an application must do.
/// </remarks>
public sealed class SystemGuidProvider : IGuidProvider
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static SystemGuidProvider Instance { get; } = new();

    /// <inheritdoc />
    public Guid NewGuid() => Guid.NewGuid();
}
