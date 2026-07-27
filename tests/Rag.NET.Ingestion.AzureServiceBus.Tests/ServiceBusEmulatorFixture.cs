using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// The Azure Service Bus emulator, over real AMQP, for the whole test collection.
/// </summary>
/// <remarks>
/// <para>
/// This is the first multi-container fixture in the repo — every other one is a single
/// container and there is no <c>docker-compose</c> anywhere. It needs two: the emulator itself
/// stores its state in SQL, so an Azure SQL Edge container joins it on a shared
/// <see cref="INetwork"/> under a fixed alias the emulator resolves via <c>SQL_SERVER</c>.
/// </para>
/// <para>
/// Entities are declared up front in a mounted <c>Config.json</c> because the emulator has no
/// management plane: there is no <c>ServiceBusAdministrationClient</c> to create a queue at
/// runtime, so anything a test wants must exist before the container starts. The file is
/// supplied through <c>WithResourceMapping</c> rather than a bind mount, which keeps the
/// fixture from writing to the developer's filesystem.
/// </para>
/// <para>
/// The connection string is the local-emulator form: <c>UseDevelopmentEmulator=true</c> with
/// the mapped AMQP port, which tells the SDK to skip TLS and the usual SAS handshake.
/// </para>
/// </remarks>
public sealed class ServiceBusEmulatorFixture : IAsyncLifetime
{
    /// <summary>A non-session queue with <c>MaxDeliveryCount</c> high enough to observe a redelivery.</summary>
    public const string QueueName = "ingestion";

    /// <summary>A session-enabled queue, for the per-document FIFO path.</summary>
    public const string SessionQueueName = "ingestion-sessions";

    private const string SqlAlias = "sqledge";
    private const string SaPassword = "R4gNet!Emulator";
    private const int AmqpPort = 5672;

    private const string ConfigJson = $$"""
        {
          "UserConfig": {
            "Namespaces": [
              {
                "Name": "sbemulatorns",
                "Queues": [
                  {
                    "Name": "{{QueueName}}",
                    "Properties": {
                      "DeadLetteringOnMessageExpiration": false,
                      "DefaultMessageTimeToLive": "PT1H",
                      "LockDuration": "PT1M",
                      "MaxDeliveryCount": 5,
                      "RequiresDuplicateDetection": false,
                      "RequiresSession": false
                    }
                  },
                  {
                    "Name": "{{SessionQueueName}}",
                    "Properties": {
                      "DeadLetteringOnMessageExpiration": false,
                      "DefaultMessageTimeToLive": "PT1H",
                      "LockDuration": "PT1M",
                      "MaxDeliveryCount": 5,
                      "RequiresDuplicateDetection": false,
                      "RequiresSession": true
                    }
                  }
                ],
                "Topics": []
              }
            ],
            "Logging": { "Type": "File" }
          }
        }
        """;

    private readonly INetwork _network = new NetworkBuilder()
        .WithName($"ragnet-servicebus-{Guid.NewGuid():N}")
        .Build();

    private IContainer _sql = null!;
    private IContainer _emulator = null!;

    /// <summary>An emulator connection string usable by <c>ServiceBusClient</c>.</summary>
    public string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _network.CreateAsync(ct);

        _sql = new ContainerBuilder("mcr.microsoft.com/azure-sql-edge:latest")
            .WithNetwork(_network)
            .WithNetworkAliases(SqlAlias)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("MSSQL_SA_PASSWORD", SaPassword)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("SQL Server is now ready for client connections"))
            .Build();
        await _sql.StartAsync(ct);

        _emulator = new ContainerBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
            .WithNetwork(_network)
            .WithPortBinding(AmqpPort, true)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("SQL_SERVER", SqlAlias)
            .WithEnvironment("MSSQL_SA_PASSWORD", SaPassword)
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(ConfigJson),
                "/ServiceBus_Emulator/ConfigFiles/Config.json")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Emulator Service is Successfully Up"))
            .Build();
        await _emulator.StartAsync(ct);

        ConnectionString =
            $"Endpoint=sb://{_emulator.Hostname}:{_emulator.GetMappedPublicPort(AmqpPort)};" +
            "SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;" +
            "UseDevelopmentEmulator=true;";
    }

    public async ValueTask DisposeAsync()
    {
        if (_emulator is not null)
            await _emulator.DisposeAsync();
        if (_sql is not null)
            await _sql.DisposeAsync();
        await _network.DisposeAsync();
    }
}
