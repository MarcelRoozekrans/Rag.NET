# Rag.NET.DataProviders.Zendesk

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.Zendesk.svg?logo=nuget&label=Rag.NET.DataProviders.Zendesk)](https://www.nuget.org/packages/Rag.NET.DataProviders.Zendesk)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.Zendesk.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.Zendesk)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Zendesk connectors for Rag.NET ingestion — two of them: support tickets via the
incremental export API, and Help Center articles, both exported as HTML with API-token
auth and a Unix-epoch `start_time` cursor.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Zendesk
```

## Setup

Register the source you need (or both — they are independent providers):

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Zendesk;

services.AddZendeskTicketsDataProvider(
    subdomain: "mycompany",
    email:     "agent@example.com",
    apiToken:  Environment.GetEnvironmentVariable("ZENDESK_API_TOKEN")!,
    configure: opts =>
    {
        opts.DeltaToken = savedTicketsCursor;  // Unix epoch; null = full export
    });

services.AddZendeskArticlesDataProvider(
    subdomain: "mycompany",
    email:     "agent@example.com",
    apiToken:  Environment.GetEnvironmentVariable("ZENDESK_API_TOKEN")!);
```

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("zendesk-tickets"), hashStore);
Console.WriteLine($"Ingested {result.Ingested}, skipped {result.Skipped}");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
