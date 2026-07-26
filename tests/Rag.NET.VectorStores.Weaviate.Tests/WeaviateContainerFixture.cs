using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Rag.NET.Weaviate.Tests;

/// <summary>
/// One anonymous-access Weaviate container for the test collection. Auto-schema is enabled
/// by default in the image (verified: new metadata properties are accepted without any
/// AUTOSCHEMA_ENABLED override), so no extra env is needed for the metadata-property tests.
/// </summary>
public sealed class WeaviateContainerFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("cr.weaviate.io/semitechnologies/weaviate:latest")
        .WithPortBinding(8080, true)
        .WithEnvironment("AUTHENTICATION_ANONYMOUS_ACCESS_ENABLED", "true")
        .WithEnvironment("PERSISTENCE_DATA_PATH", "/var/lib/weaviate")
        .WithEnvironment("DEFAULT_VECTORIZER_MODULE", "none")
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/v1/.well-known/ready")))
        .Build();

    public Uri Endpoint { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        Endpoint = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}");
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
