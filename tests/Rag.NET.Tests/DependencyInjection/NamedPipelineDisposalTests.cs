using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public sealed class NamedPipelineDisposalTests
{
    private sealed class AsyncOnlyDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static IEmbeddingGenerator<string, Embedding<float>> Embedder() =>
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    /// <summary>Disposing the factory disposes each child's async-only services, and does not throw.</summary>
    /// <remarks>
    /// <para>
    /// <c>ServiceProvider.Dispose()</c> throws when it holds a service implementing only
    /// <see cref="IAsyncDisposable"/>. Seven types in the per-pipeline surface do, so getting this
    /// wrong is a crash at shutdown rather than a leak.
    /// </para>
    /// <para>
    /// Registered by <b>type</b> (<c>AddSingleton&lt;AsyncOnlyDisposable&gt;()</c>), not by a
    /// pre-built instance: a plain <c>ServiceProvider</c> never disposes a singleton registered as
    /// an already-constructed instance, only one it constructed itself — confirmed directly against
    /// Microsoft.Extensions.DependencyInjection 10.0.11. Registering an instance here would make
    /// this test fail regardless of whether the factory disposes its children correctly.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_DisposesEachChildsAsyncOnlyServices()
    {
        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Embedder()));
        services.AddRagNet("docs", rag =>
        {
            rag.Services.AddSingleton(Substitute.For<IVectorStore>());
            rag.Services.AddSingleton<AsyncOnlyDisposable>();
        });

        var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();
        var docsResource = factory.ProviderFor("docs").GetRequiredService<AsyncOnlyDisposable>();

        await factory.DisposeAsync();

        Assert.True(docsResource.Disposed);
    }

    /// <summary>A shared service is not disposed by a child.</summary>
    /// <remarks>
    /// <para>
    /// Ownership runs one way. If a child disposed what it merely forwards, tearing down one
    /// pipeline would pull the embedding model out from under every other one.
    /// </para>
    /// <para>
    /// Registered by <b>type</b>, for the same reason as the test above: only a singleton the root
    /// container constructed itself is disposed when the root is disposed, so proving "the root
    /// still owns it" requires resolving it through the root, not through a captured pre-built
    /// instance.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_DoesNotDisposeSharedServices()
    {
        var services = new ServiceCollection();
        services.AddRagNetShared(rag =>
        {
            rag.Services.AddSingleton(Embedder());
            rag.Services.AddSingleton<AsyncOnlyDisposable>();
        });
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();
        var sharedResource = factory.ProviderFor("docs").GetRequiredService<AsyncOnlyDisposable>();

        await factory.DisposeAsync();

        Assert.False(sharedResource.Disposed);

        await provider.DisposeAsync();
        Assert.True(sharedResource.Disposed);
    }

    [Fact]
    public async Task Get_AfterDispose_Throws()
    {
        var services = new ServiceCollection();
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();

        await factory.DisposeAsync();

        _ = Assert.Throws<ObjectDisposedException>(() => factory.Get("docs"));
        await provider.DisposeAsync();
    }

    /// <summary>
    /// Synchronously disposing a child that holds an async-only service throws — and that is the
    /// intended contract, not a bug. Disposing the same shape of child asynchronously does not.
    /// </summary>
    /// <remarks>
    /// This matches plain <c>ServiceProvider</c>, which throws
    /// <see cref="InvalidOperationException"/> from <c>Dispose()</c> when it holds a service that
    /// implements only <see cref="IAsyncDisposable"/>, telling the caller to use
    /// <c>DisposeAsync</c> instead. <see cref="RagPipelineFactory"/> is deliberately not adding a
    /// sync-over-async fallback here: that is what a .NET developer already expects from
    /// <c>ServiceProvider</c>, and sync-over-async during disposal can deadlock. This is reachable
    /// in practice — <c>SqliteAuditLog</c> (<c>Rag.NET.Security.Audit.Sqlite</c>) is declared
    /// <c>: IAuditLog, IAsyncDisposable</c> with no <c>IDisposable</c>, so a named pipeline
    /// configured with <c>UseSqliteAuditLog</c> hits exactly this path.
    /// </remarks>
    [Fact]
    public async Task Dispose_WithAnAsyncOnlyChildService_Throws()
    {
        var services = new ServiceCollection();
        services.AddRagNet("docs", rag =>
        {
            rag.Services.AddSingleton(Substitute.For<IVectorStore>());
            rag.Services.AddSingleton<AsyncOnlyDisposable>();
        });

        var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();
        _ = factory.ProviderFor("docs").GetRequiredService<AsyncOnlyDisposable>();

        _ = Assert.Throws<InvalidOperationException>(factory.Dispose);

        await provider.DisposeAsync();
    }

    /// <summary>The asynchronous counterpart of the test above: the same shape does not throw.</summary>
    [Fact]
    public async Task DisposeAsync_WithAnAsyncOnlyChildService_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddRagNet("docs", rag =>
        {
            rag.Services.AddSingleton(Substitute.For<IVectorStore>());
            rag.Services.AddSingleton<AsyncOnlyDisposable>();
        });

        var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();
        _ = factory.ProviderFor("docs").GetRequiredService<AsyncOnlyDisposable>();

        await factory.DisposeAsync();

        await provider.DisposeAsync();
    }
}
