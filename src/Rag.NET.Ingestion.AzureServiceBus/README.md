# Rag.NET.Ingestion.AzureServiceBus

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Ingestion.AzureServiceBus.svg?logo=nuget&label=Rag.NET.Ingestion.AzureServiceBus)](https://www.nuget.org/packages/Rag.NET.Ingestion.AzureServiceBus)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Ingestion.AzureServiceBus.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Ingestion.AzureServiceBus)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
