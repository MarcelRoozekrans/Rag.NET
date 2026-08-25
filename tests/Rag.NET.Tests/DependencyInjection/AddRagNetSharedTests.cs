using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public sealed class AddRagNetSharedTests
{
    private interface IThing;

    private sealed class Thing : IThing;

    [Fact]
    public void AddRagNetShared_RegistersIntoTheRootCollection()
    {
        var services = new ServiceCollection();

        services.AddRagNetShared(rag => rag.Services.AddSingleton<IThing, Thing>());

        using var provider = services.BuildServiceProvider();
        Assert.IsType<Thing>(provider.GetRequiredService<IThing>());
    }

    [Fact]
    public void AddRagNetShared_RecordsTheServiceTypesItRegistered()
    {
        var services = new ServiceCollection();

        services.AddRagNetShared(rag => rag.Services.AddSingleton<IThing, Thing>());

        using var provider = services.BuildServiceProvider();
        var shared = provider.GetRequiredService<SharedServiceTypes>();
        Assert.Contains(typeof(IThing), shared.Types);
    }

    /// <summary>
    /// It records only what its own callback added, not what was already on the collection.
    /// </summary>
    /// <remarks>
    /// The root collection also holds the host's logging, configuration and HttpClients. Forwarding
    /// those into every child would make each pipeline depend on the host's container shape, which
    /// is exactly why sharing is a declared block rather than inferred from the outer collection.
    /// </remarks>
    [Fact]
    public void AddRagNetShared_DoesNotRecordPreexistingRegistrations()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IThing, Thing>();

        services.AddRagNetShared(rag => rag.Services.AddSingleton<Thing>());

        using var provider = services.BuildServiceProvider();
        var shared = provider.GetRequiredService<SharedServiceTypes>();
        Assert.DoesNotContain(typeof(IThing), shared.Types);
        Assert.Contains(typeof(Thing), shared.Types);
    }

    /// <summary>
    /// It does not register a pipeline. Sharing a model is not running a pipeline in the root.
    /// </summary>
    /// <remarks>
    /// If this called <c>AddRagNETServices()</c> the root would build its own stores alongside every
    /// child's — paying for a pipeline nobody asked for, and muddying which store a forwarded
    /// resolve reaches.
    /// </remarks>
    [Fact]
    public void AddRagNetShared_DoesNotRegisterAPipeline()
    {
        var services = new ServiceCollection();

        services.AddRagNetShared(rag => rag.Services.AddSingleton<IThing, Thing>());

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IRagPipeline>());
    }
}
