using Microsoft.Extensions.AI;

namespace Rag.NET.Models.Options;

/// <summary>
/// Options for <c>UseFallbackChain</c>: an ordered list of chat-client factories tried
/// first-to-last on transient failure, plus an optional per-client timeout.
/// </summary>
/// <remarks>
/// Clients are supplied as factories so each per-provider client can be built from the
/// service provider without the chain wrapping itself: <c>UseFallbackChain</c> supersedes
/// any prior <c>IChatClient</c> registration, so resolving <c>IChatClient</c> inside a
/// factory would recurse into the chain — construct the provider client directly instead.
/// </remarks>
public sealed class FallbackChainOptions
{
    /// <summary>
    /// Ordered client factories; the first is the primary, subsequent entries are tried
    /// on transient failure. At least 2 are required.
    /// </summary>
    public IList<Func<IServiceProvider, IChatClient>> Clients { get; } = [];

    /// <summary>
    /// Optional upper bound for each per-client attempt. When it elapses, the attempt is
    /// treated as a transient failure and the next client is tried (caller cancellation
    /// still propagates immediately). Must be greater than zero when set; <see langword="null"/>
    /// (default) means attempts are unbounded.
    /// </summary>
    public TimeSpan? PerClientTimeout { get; set; }

    /// <summary>Adds a client factory to the end of the chain.</summary>
    /// <param name="factory">Builds the client from the service provider.</param>
    public FallbackChainOptions AddClient(Func<IServiceProvider, IChatClient> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Clients.Add(factory);
        return this;
    }
}
