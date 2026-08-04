# Rag.NET.Ingestion.AzureServiceBus

Azure Service Bus ingestion trigger for Rag.NET: consumes messages from a queue or
subscription, ingests each one end-to-end, and settles it on the outcome — complete on
success, abandon for retry, dead-letter when poisonous — so a crash mid-ingest means
redelivery, not loss.

## Install

```bash
dotnet add package Rag.NET.Ingestion.AzureServiceBus
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the trigger registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Ingestion.AzureServiceBus;

rag.UseServiceBusIngestion(
    connectionString: Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION_STRING")!,
    queueName:        "rag-ingest");
```

An overload takes a fully qualified namespace + `TokenCredential` for managed identity.

## Example

Concurrency and lock renewal are the operational knobs:

```csharp
using Rag.NET.Ingestion.AzureServiceBus;

rag.UseServiceBusIngestion(
    connectionString: Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION_STRING")!,
    queueName:        "rag-ingest",
    configure: o =>
    {
        o.MaxConcurrentCalls          = 4;                        // default 1
        o.MaxAutoLockRenewalDuration  = TimeSpan.FromMinutes(10); // default 5, for slow documents
    });
```

The trigger deliberately bypasses the in-memory job queue: it settles each broker message
only after the ingest outcome is known, keeping Service Bus's at-least-once guarantee.

## Full guide

- [Event-driven ingestion](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
