# Rag.NET.DataProviders.Confluence

Confluence Cloud connector for Rag.NET ingestion: enumerates pages (optionally per space)
as HTML, authenticated with email + API token, with a CQL `lastModified` cursor for
incremental runs.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Confluence
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Confluence;

services.AddConfluenceDataProvider(
    baseUrl:  "https://your-domain.atlassian.net/wiki",
    email:    "user@example.com",
    apiToken: Environment.GetEnvironmentVariable("CONFLUENCE_API_TOKEN")!,
    configure: opts =>
    {
        opts.SpaceKey   = "ENG";            // null = all spaces
        opts.DeltaToken = savedDeltaToken;  // null on first run = full traversal
    });
```

Pages arrive as HTML — register `AddHtmlParser()` from `Rag.NET.Parsers.Html` in your
pipeline.

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("confluence"), hashStore);

Console.WriteLine($"Ingested {result.Ingested}, skipped {result.Skipped}");
// Persist the new lastModified cursor for the next run after an error-free run.
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
