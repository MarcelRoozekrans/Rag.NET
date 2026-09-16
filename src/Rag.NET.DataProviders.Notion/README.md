# Rag.NET.DataProviders.Notion

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.Notion.svg?logo=nuget&label=Rag.NET.DataProviders.Notion)](https://www.nuget.org/packages/Rag.NET.DataProviders.Notion)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.Notion.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.Notion)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
