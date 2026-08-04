# Rag.NET.DataProviders.Notion

Notion connector for Rag.NET ingestion: exports the pages your integration can see as
Markdown, authenticated with an integration token, filtering incrementally on each page's
`last_edited_time`.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Notion
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Notion;

services.AddNotionDataProvider(
    integrationToken: Environment.GetEnvironmentVariable("NOTION_TOKEN")!,
    configure: opts =>
    {
        opts.DeltaToken = savedDeltaToken;  // ISO 8601 last_edited_time; null = full traversal
    });
```

Share the target pages/databases with your integration in Notion — the API only returns
what the integration was granted.

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("notion"), hashStore);
Console.WriteLine($"Ingested {result.Ingested} Markdown pages");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
