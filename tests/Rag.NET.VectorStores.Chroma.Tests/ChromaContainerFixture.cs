using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Rag.NET.Chroma.Tests;

/// <summary>
/// One anonymous-access Chroma container for the test collection (each test isolates
/// itself in a uniquely named collection). The image serves the REST v2 API on port 8000;
/// readiness is the v2 heartbeat.
/// </summary>
public sealed class ChromaContainerFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("chromadb/chroma:latest")
        .WithPortBinding(8000, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(8000).ForPath("/api/v2/heartbeat")))
        .Build();

    public Uri Endpoint { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        Endpoint = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8000)}");
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
