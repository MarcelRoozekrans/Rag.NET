using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// Registration, following the <c>UseEventDrivenIngestionTests</c> / <c>UsePollingIngestion</c>
/// shape. Nothing here contacts a broker: <c>ServiceBusClient</c> parses its connection string
/// eagerly but does not connect, and creating a processor opens no link.
/// </summary>
public sealed class UseServiceBusIngestionTests
{
    private const string ConnectionString =
        "Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=abc123=";

    private const string Namespace = "contoso.servicebus.windows.net";

    private static RagBuilder BuilderWithIngestor(ServiceCollection services)
    {
        services.AddSingleton<IIngestor>(FakeIngestor.Succeeding());
        return new RagBuilder(services);
    }

    [Fact]
    public async Task ConnectionStringOverload_RegistersOneHostedService()
    {
        var services = new ServiceCollection();
        BuilderWithIngestor(services).UseServiceBusIngestion(ConnectionString, "queue-a");

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.Single(hosted);
        Assert.IsType<AzureServiceBusIngestionTrigger>(hosted[0]);
    }

    [Fact]
    public async Task TokenCredentialOverload_RegistersOneHostedService()
    {
        var services = new ServiceCollection();
        BuilderWithIngestor(services)
            .UseServiceBusIngestion(Namespace, new FakeCredential(), "queue-a");

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.Single(hosted);
        Assert.IsType<AzureServiceBusIngestionTrigger>(hosted[0]);
    }

    [Fact]
    public async Task TwoRegistrations_YieldTwoIndependentTriggers()
    {
        var services = new ServiceCollection();
        var builder = BuilderWithIngestor(services);
        builder.UseServiceBusIngestion(ConnectionString, "queue-a");
        builder.UseServiceBusIngestion(Namespace, new FakeCredential(), "queue-b",
            o => o.SessionsEnabled = true);

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.Equal(2, hosted.Count);
        Assert.All(hosted, h => Assert.IsType<AzureServiceBusIngestionTrigger>(h));
        // Each registration closes over its own options instance, so one queue with sessions
        // and one without coexist rather than fighting over a shared singleton.
        Assert.NotSame(hosted[0], hosted[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankConnectionString_Throws(string? connectionString)
    {
        var builder = BuilderWithIngestor(new ServiceCollection());

        // ThrowsAny: a null argument surfaces as ArgumentNullException, a blank one as
        // ArgumentException. Both are the same registration-time refusal.
        Assert.ThrowsAny<ArgumentException>(() =>
            builder.UseServiceBusIngestion(connectionString!, "queue-a"));
    }

    [Fact]
    public void MalformedConnectionString_ThrowsAtRegistration()
    {
        var builder = BuilderWithIngestor(new ServiceCollection());

        // Fail fast and loud, matching every other Use* extension: a bad connection string is
        // a configuration error, not something to discover at the first receive. The SDK
        // raises FormatException rather than ArgumentException for this — pinned so a future
        // change to eager client construction cannot silently defer the failure.
        Assert.Throws<FormatException>(() =>
            builder.UseServiceBusIngestion("this-is-not-a-connection-string", "queue-a"));
    }

    [Fact]
    public void BlankQueueName_Throws()
    {
        var builder = BuilderWithIngestor(new ServiceCollection());

        Assert.Throws<ArgumentException>(() => builder.UseServiceBusIngestion(ConnectionString, "  "));
    }

    [Fact]
    public void NullCredential_Throws()
    {
        var builder = BuilderWithIngestor(new ServiceCollection());

        Assert.Throws<ArgumentNullException>(() =>
            builder.UseServiceBusIngestion(Namespace, null!, "queue-a"));
    }

    [Fact]
    public void BlankSubscriptionName_Throws()
    {
        var builder = BuilderWithIngestor(new ServiceCollection());

        Assert.Throws<ArgumentException>(() =>
            builder.UseServiceBusIngestion(ConnectionString, "topic-a", o => o.SubscriptionName = " "));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMaxConcurrentCalls_Throws(int value)
    {
        var builder = BuilderWithIngestor(new ServiceCollection());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.UseServiceBusIngestion(ConnectionString, "queue-a", o => o.MaxConcurrentCalls = value));
    }

    [Fact]
    public void NonPositiveMaxConcurrentSessions_Throws()
    {
        var builder = BuilderWithIngestor(new ServiceCollection());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.UseServiceBusIngestion(ConnectionString, "queue-a", o => o.MaxConcurrentSessions = 0));
    }

    [Fact]
    public void NegativePrefetchCount_Throws()
    {
        var builder = BuilderWithIngestor(new ServiceCollection());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.UseServiceBusIngestion(ConnectionString, "queue-a", o => o.PrefetchCount = -1));
    }

    [Fact]
    public void NegativeLockRenewalDuration_Throws()
    {
        var builder = BuilderWithIngestor(new ServiceCollection());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.UseServiceBusIngestion(ConnectionString, "queue-a",
                o => o.MaxAutoLockRenewalDuration = TimeSpan.FromSeconds(-1)));
    }
}
