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
/// <para>
/// This test project multi-targets, and <c>dotnet test</c> runs the target frameworks as
/// separate processes at the same time, so both would race for those fixed ports. The
/// fixture therefore holds an exclusive lock file for as long as the container lives, which
/// makes a second process wait until the first has released the ports.
/// </para>
/// </summary>
public sealed class PineconeContainerFixture : IAsyncLifetime
{
    private const int ControlPlanePort = 5080;
    private const int LastIndexPort = 5090;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(15);

    private readonly IContainer _container = BuildContainer();
    private FileStream? _portLock;

    /// <summary>Control-plane endpoint, e.g. <c>http://localhost:5080</c>.</summary>
    public Uri Endpoint { get; } = new($"http://localhost:{ControlPlanePort}");

    public async ValueTask InitializeAsync()
    {
        _portLock = await AcquirePortLockAsync(TestContext.Current.CancellationToken);
        await _container.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _container.DisposeAsync();
        }
        finally
        {
            if (_portLock is not null)
                await _portLock.DisposeAsync();
        }
    }

    private static async Task<FileStream> AcquirePortLockAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), "ragnet-pinecone-local-ports.lock");
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
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
