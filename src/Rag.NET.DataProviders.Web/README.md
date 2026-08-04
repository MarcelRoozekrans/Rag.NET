# Rag.NET.DataProviders.Web

Web content providers for Rag.NET ingestion — no credentials, three shapes: a sitemap
walker, an RSS/Atom feed reader, and a same-domain crawler with depth, page-count and
robots.txt controls.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Web
```

## Setup

These providers are constructed directly — no DI extension method:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders.Web;

var httpClient = new HttpClient();
var provider = new SitemapDataProvider("https://docs.example.com/sitemap.xml", httpClient);
services.AddSingleton<IFileContentProvider>(provider);
```

Pages arrive as HTML — register `AddHtmlParser()` from `Rag.NET.Parsers.Html` in your
pipeline.

## Example

The crawler variant, bounded so it cannot wander off:

```csharp
using Rag.NET.DataProviders.Web;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var crawler = new WebCrawlerDataProvider("https://docs.example.com", httpClient, new WebCrawlerOptions
{
    MaxDepth         = 3,
    MaxPages         = 500,
    SameDomain       = true,
    RespectRobotsTxt = true,
});

var result = await pipeline.IngestFromProviderAsync(crawler, new ProviderId("docs-site"), hashStore);
Console.WriteLine($"Ingested {result.Ingested} pages");
```

`RssDataProvider` follows the same pattern for feeds.

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
