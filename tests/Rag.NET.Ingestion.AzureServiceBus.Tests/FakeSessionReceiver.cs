using Azure.Messaging.ServiceBus;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// Exists only to satisfy the <see cref="ProcessSessionMessageEventArgs"/> constructor.
/// </summary>
internal sealed class FakeSessionReceiver : ServiceBusSessionReceiver;
