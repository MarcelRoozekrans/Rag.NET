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
internal sealed class RagPipelineFactory(
    IReadOnlyDictionary<string, IServiceCollection> collections) : IRagPipelineFactory, IDisposable, IAsyncDisposable
{
    private readonly Dictionary<string, ServiceProvider> _providers = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <inheritdoc />
    public bool Contains(string name) => collections.ContainsKey(name);

    /// <inheritdoc />
    public IRagPipeline Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return ProviderFor(name).GetRequiredService<IRagPipeline>();
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
