using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Rag.NET.Pinecone.Tests;

/// <summary>
/// One Pinecone Local database emulator for the test collection (each test isolates
/// itself in uniquely named indexes and deletes them afterwards). The emulator serves
/// the control plane on port 5080 and allocates one data-plane port per index from
/// 5081–5090; index hosts are advertised as <c>localhost:{containerPort}</c>
/// (<c>PINECONE_HOST=localhost</c>), so the whole range is bound to the SAME host
/// ports — a dynamic mapping would break the advertised host:port. Consequence: at
/// most ten indexes may exist at once (tests clean up after themselves) and the suite
/// needs ports 5080–5090 free on the host.
/// </summary>
public sealed class PineconeContainerFixture : IAsyncLifetime
{
    private const int ControlPlanePort = 5080;
    private const int LastIndexPort = 5090;

    private readonly IContainer _container = BuildContainer();

    /// <summary>Control-plane endpoint, e.g. <c>http://localhost:5080</c>.</summary>
    public Uri Endpoint { get; } = new($"http://localhost:{ControlPlanePort}");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    private static IContainer BuildContainer()
    {
        var builder = new ContainerBuilder("ghcr.io/pinecone-io/pinecone-local:latest")
            .WithEnvironment("PORT", ControlPlanePort.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("PINECONE_HOST", "localhost")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(ControlPlanePort).ForPath("/indexes")));
        for (var port = ControlPlanePort; port <= LastIndexPort; port++)
            builder = builder.WithPortBinding(port, port);
        return builder.Build();
    }
}
