using Azure.Messaging.ServiceBus;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// Exists only to satisfy the <see cref="ProcessMessageEventArgs"/> constructor.
/// <see cref="ServiceBusReceiver"/> has a protected parameterless constructor for exactly this.
/// </summary>
internal sealed class FakeReceiver : ServiceBusReceiver;
