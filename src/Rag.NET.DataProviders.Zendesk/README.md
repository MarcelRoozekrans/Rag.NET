# Rag.NET.DataProviders.Zendesk

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
