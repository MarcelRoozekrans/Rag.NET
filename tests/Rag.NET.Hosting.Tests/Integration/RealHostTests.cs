using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.NET.Abstractions;
using Rag.NET.Hosting.DependencyInjection;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Hosting.Tests.Integration;

/// <summary>
/// Builds and starts a <b>real</b> <see cref="IHost"/> around
/// <c>AddRagNetPipelineFromConfiguration</c>, and resolves through the host's own provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Milestone 6, Phase 6.2 (§2(d) of the phase design).</b> The rest of this project calls
/// <c>services.BuildServiceProvider()</c> on a bare <see cref="ServiceCollection"/>. That is a
/// container, not a host, and it skips everything a host adds: the real configuration pipeline,
/// <c>ValidateOnBuild</c> and <c>ValidateScopes</c> (both on by default in the Development
/// environment), hosted-service startup, and the disposal path on shutdown. A registration that
/// resolves fine from a bare provider and throws on a real host's validation would pass every
/// existing test in this project.
/// </para>
/// <para>
/// This package's whole job is to be hosted, so "does it survive being hosted" is the one question
/// its tests were not asking.
/// </para>
/// <para>
/// No network call is made. The endpoints below are configured but never dialled — the assertions
/// are about the host starting and the pipeline resolving, not about a provider's response.
/// </para>
/// </remarks>
public sealed class RealHostTests
{
    [Fact]
    public async Task AHostConfiguredFromConfiguration_StartsAndResolvesThePipeline()
    {
        using var host = BuildHost();

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // Resolved from the running host's provider, not a provider built on the side.
            var pipeline = host.Services.GetRequiredService<IRagPipeline>();
            Assert.NotNull(pipeline);

            var store = host.Services.GetRequiredService<IVectorStore>();
            Assert.IsType<InMemoryVectorStore>(store);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <remarks>
    /// The Development environment turns on <c>ValidateOnBuild</c> and <c>ValidateScopes</c>.
    /// Building the host under it is the assertion: a captive dependency or an unresolvable
    /// registration throws here and nowhere else in this project's suite.
    /// </remarks>
    [Fact]
    public void TheHostBuildsUnderScopeAndBuildTimeValidation()
    {
        using var host = BuildHost(environment: Environments.Development);

        Assert.NotNull(host.Services);
    }

    /// <remarks>
    /// A pipeline resolved inside a scope, which is how a hosted consumer (a controller, a hosted
    /// service, the CLI's command scope) actually gets one. A singleton capturing a scoped
    /// dependency shows up here and not in a root-only resolution.
    /// </remarks>
    [Fact]
    public async Task ThePipelineResolvesInsideAScope()
    {
        using var host = BuildHost(environment: Environments.Development);
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var scope = host.Services.CreateScope();
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRagPipeline>());
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static IHost BuildHost(string? environment = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment ?? Environments.Production,
            // No content root scanning for an appsettings.json that differs per machine.
            Args = [],
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["RagNet:ChatClient:Endpoint"] = "https://openrouter.ai/api/v1",
            ["RagNet:ChatClient:ApiKey"] = "test-key",
            ["RagNet:ChatClient:Model"] = "meta-llama/llama-3.3-70b-instruct",
            ["RagNet:Embeddings:Endpoint"] = "https://openrouter.ai/api/v1",
            ["RagNet:Embeddings:ApiKey"] = "test-key",
            ["RagNet:Embeddings:Model"] = "text-embedding-3-small",
            ["RagNet:Embeddings:VectorDimensions"] = "1536",
            ["RagNet:VectorStore:Kind"] = "InMemory",
        });

        builder.Services.AddRagNetPipelineFromConfiguration(builder.Configuration);
        return builder.Build();
    }
}
