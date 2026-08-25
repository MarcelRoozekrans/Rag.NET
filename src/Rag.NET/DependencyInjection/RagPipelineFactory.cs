using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.DependencyInjection;

/// <summary>Builds and owns one child <see cref="IServiceProvider"/> per named pipeline.</summary>
/// <remarks>
/// <para>
/// <b>Children are built lazily, on first <see cref="Get"/>.</b> At registration time the root
/// provider does not exist, so there is nothing for a child's forwarded services to resolve from.
/// The cost is that a misconfigured named pipeline surfaces on first use rather than at startup.
/// </para>
/// <para>
/// <b>Ownership runs one way.</b> Shared services live in the root and are disposed by it, never by
/// a child — so tearing down one pipeline cannot pull the embedding model out from under another.
/// Both <see cref="Dispose"/> and <see cref="DisposeAsync"/> are supported because this factory is
/// itself a root-container singleton: a host that disposes its root provider synchronously must be
/// able to tear this down synchronously too. Each child is a concrete <c>ServiceProvider</c>, which
/// supports both; a child whose own registrations include a service that implements only
/// <see cref="IAsyncDisposable"/> will still make synchronous disposal throw for that child, exactly
/// as a plain <c>ServiceProvider</c> would.
/// </para>
/// <para>
/// <b>One throwing child does not orphan the rest.</b> Every child is disposed even when an earlier
/// one throws — failures are collected and re-raised only after every child has had its chance,
/// so a named pipeline that hits the case above cannot prevent the others from releasing what they
/// hold.
/// </para>
/// </remarks>
/// <param name="collections">Each name's composed service collection.</param>
/// <param name="sharedServiceTypes">
/// The service types <c>AddRagNetShared</c> declared, used only to explain a resolution failure —
/// see <see cref="Get"/>. Empty is a legitimate state and is the most informative thing the message
/// can say when it happens (#390).
/// </param>
internal sealed class RagPipelineFactory(
    IReadOnlyDictionary<string, IServiceCollection> collections,
    IReadOnlyList<Type> sharedServiceTypes) : IRagPipelineFactory, IDisposable, IAsyncDisposable
{
    private readonly Dictionary<string, ServiceProvider> _providers = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <inheritdoc />
    public bool Contains(string name) => collections.ContainsKey(name);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The pipeline could not be resolved from its own container — most often a service that is
    /// registered on the root <see cref="IServiceCollection"/> but was never declared shared, so no
    /// named pipeline can see it. The container's own message is the inner exception; this one adds
    /// where the service has to be registered instead (#390).
    /// </exception>
    public IRagPipeline Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var provider = ProviderFor(name);
        try
        {
            return provider.GetRequiredService<IRagPipeline>();
        }
        catch (InvalidOperationException ex)
        {
            // The container names the missing type and stops there, which sends the reader looking
            // for a registration that is often already present — on the root collection, where a
            // named pipeline cannot see it. Reported as #390 against a container that registered an
            // IVectorStore, a chunker, caching and a parser, but no embedding generator anywhere.
            throw new InvalidOperationException(ExplainResolutionFailure(name, ex), ex);
        }
    }

    /// <summary>
    /// Turns a container resolution failure into a message that says where to put the registration.
    /// </summary>
    /// <remarks>
    /// The missing type is deliberately not parsed out of <paramref name="inner"/>. The container's
    /// message already names it and is carried as the inner exception; recovering a
    /// <see cref="Type"/> from that text would need an assembly-qualified name it does not contain,
    /// so it would work for this library's own types and fail for the ones most likely to be
    /// missing — an embedding generator or chat client from another package.
    /// </remarks>
    /// <param name="name">The pipeline being resolved.</param>
    /// <param name="inner">The container's own failure.</param>
    /// <returns>The explanatory message.</returns>
    private string ExplainResolutionFailure(string name, InvalidOperationException inner)
    {
        var shared = sharedServiceTypes.Count == 0
            ? "nothing — AddRagNetShared was never called, or declared no services"
            : string.Join(", ", sharedServiceTypes.Select(t => t.Name));

        return $"""
            The RAG pipeline named '{name}' could not be resolved: {inner.Message}

            A named pipeline resolves from its own container, which is built from:
              - its own AddRagNet("{name}", rag => ...) block,
              - anything AddRagNetShared(rag => ...) declared, currently: {shared}, and
              - the host's own singleton registrations on IServiceCollection, which it inherits
                unless its own block registers the same type.

            So nothing anywhere provides this service. Register it in whichever of those three
            places fits: the named block if only this pipeline needs it, AddRagNetShared if every
            pipeline should share one instance, or the collection directly — for example
            services.AddEmbeddingGenerator(...) — if the host owns it.
            """;
    }

    /// <summary>The child provider for <paramref name="name"/>, building it on first use.</summary>
    /// <param name="name">The pipeline name.</param>
    /// <returns>That name's provider.</returns>
    /// <exception cref="ArgumentException">No pipeline was registered under that name.</exception>
    internal ServiceProvider ProviderFor(string name)
    {
        lock (_lock)
        {
            if (_providers.TryGetValue(name, out var existing))
            {
                return existing;
            }

            if (!collections.TryGetValue(name, out var collection))
            {
                throw new ArgumentException(
                    $"No RAG pipeline is registered under the name '{name}'. "
                    + "Register one with services.AddRagNet(\"" + name + "\", rag => …).",
                    nameof(name));
            }

            var built = collection.BuildServiceProvider();
            _providers[name] = built;
            return built;
        }
    }

    /// <inheritdoc />
    /// <exception cref="Exception">
    /// One child's disposal exception, or an <see cref="AggregateException"/> wrapping more than
    /// one, re-raised only after every child was given the chance to dispose.
    /// </exception>
    public void Dispose()
    {
        var toDispose = TakeProvidersToDispose();
        if (toDispose is null)
        {
            return;
        }

        List<Exception>? failures = null;
        foreach (ref readonly var provider in CollectionsMarshal.AsSpan(toDispose))
        {
            try
            {
                provider.Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        ThrowIfAny(failures);
    }

    /// <inheritdoc />
    /// <exception cref="Exception">
    /// One child's disposal exception, or an <see cref="AggregateException"/> wrapping more than
    /// one, re-raised only after every child was given the chance to dispose.
    /// </exception>
    public async ValueTask DisposeAsync()
    {
        var toDispose = TakeProvidersToDispose();
        if (toDispose is null)
        {
            return;
        }

        // A Span (CollectionsMarshal.AsSpan, used by the synchronous Dispose above) is a ref
        // struct and cannot be held across an await, so this path indexes the list instead.
        List<Exception>? failures = null;
        for (var i = 0; i < toDispose.Count; i++)
        {
            try
            {
                await toDispose[i].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        ThrowIfAny(failures);
    }

    /// <summary>
    /// Marks this factory disposed and hands back the children to dispose, under the same lock
    /// <see cref="ProviderFor"/> uses — both were previously read and mutated unlocked.
    /// </summary>
    /// <returns>
    /// The children built so far, or <see langword="null"/> if this factory was already disposed.
    /// </returns>
    private List<ServiceProvider>? TakeProvidersToDispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return null;
            }

            _disposed = true;
            var toDispose = new List<ServiceProvider>(_providers.Values);
            _providers.Clear();
            return toDispose;
        }
    }

    /// <summary>Re-raises collected disposal failures, preserving a single exception's own stack.</summary>
    /// <param name="failures">The exceptions collected while disposing each child, if any.</param>
    private static void ThrowIfAny(List<Exception>? failures)
    {
        switch (failures)
        {
            case null or { Count: 0 }:
                return;
            case { Count: 1 }:
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
                break;
            default:
                throw new AggregateException(failures);
        }
    }
}
