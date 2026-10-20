using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Rag.NET.Testing;

public sealed class QdrantFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("qdrant/qdrant:latest")
        .WithPortBinding(6333, true)
        .WithPortBinding(6334, true)
        // /readyz answers on the REST port; GrpcPort is what callers use, and Qdrant starts that
        // listener separately, a moment after REST. Wait for both.
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(6333).ForPath("/readyz"))
            .UntilMessageIsLogged("Qdrant gRPC listening on 6334"))
        .Build();

    public string Host => _container.Hostname;
    public int GrpcPort => _container.GetMappedPublicPort(6334);

    public async ValueTask InitializeAsync() =>
        await _container.StartAsync();

    public async ValueTask DisposeAsync() =>
        await _container.DisposeAsync();
}
