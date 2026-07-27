using Xunit;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>Shares one emulator across the integration tests — two containers is a slow start.</summary>
[CollectionDefinition(Name)]
public sealed class ServiceBusEmulatorCollection : ICollectionFixture<ServiceBusEmulatorFixture>
{
    public const string Name = "ServiceBusEmulator";
}
