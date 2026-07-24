using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders.EventDriven;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.DataProviders.Tests.EventDriven;

public sealed class UseEventDrivenIngestionTests
{
    [Fact]
    public void RegistersQueueOptionsAndHostedProcessor()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseEventDrivenIngestion(o => o.QueueCapacity = 5);

        Assert.Single(services, d => d.ServiceType == typeof(IIngestionJobQueue));
        Assert.Single(services, d => d.ServiceType == typeof(EventDrivenIngestionOptions));
        var hosted = Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(typeof(IngestionJobProcessor), hosted.ImplementationType);
    }

    [Fact]
    public void ResolvesChannelQueueAndProcessor()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IIngestor>());
        new RagBuilder(services).UseEventDrivenIngestion();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ChannelIngestionJobQueue>(provider.GetRequiredService<IIngestionJobQueue>());
        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        Assert.IsType<IngestionJobProcessor>(hosted);
    }

    [Fact]
    public void CalledTwice_IsIdempotent_FirstRegistrationWins()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IIngestor>());
        var builder = new RagBuilder(services);

        builder.UseEventDrivenIngestion(o => o.QueueCapacity = 5);
        builder.UseEventDrivenIngestion(o => o.QueueCapacity = 9);

        Assert.Single(services, d => d.ServiceType == typeof(IIngestionJobQueue));
        Assert.Single(services, d => d.ServiceType == typeof(EventDrivenIngestionOptions));
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));

        using var provider = services.BuildServiceProvider();
        Assert.Equal(5, provider.GetRequiredService<EventDrivenIngestionOptions>().QueueCapacity);
        Assert.IsType<ChannelIngestionJobQueue>(provider.GetRequiredService<IIngestionJobQueue>());
    }

    [Fact]
    public void NonPositiveQueueCapacity_Throws()
    {
        var builder = new RagBuilder(new ServiceCollection());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.UseEventDrivenIngestion(o => o.QueueCapacity = 0));
    }
}
