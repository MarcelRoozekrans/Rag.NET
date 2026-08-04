# Rag.NET.DataProviders

The connector infrastructure for Rag.NET ingestion: the shared token providers
(`StaticTokenProvider`, OAuth client-credentials refresh), resilient HTTP client wiring,
and the pull/push ingestion triggers every `Rag.NET.DataProviders.*` connector builds on.

## Install

```bash
dotnet add package Rag.NET.DataProviders
```

You normally install a concrete connector (`Rag.NET.DataProviders.Confluence`,
`.GitHub`, `.AzureBlob`, …) and get this package transitively; install it directly when
writing your own `IFileContentProvider`.

## Setup

Background ingestion triggers register inside the `AddRagNet(...)` builder callback:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DependencyInjection;

services.AddRagNet(rag => rag
    .UseEventDrivenIngestion(o => o.QueueCapacity = 500)); // bounded, backpressure on full
```

## Example

Token providers cover the two common auth shapes — a fixed key, or OAuth2 client
credentials with automatic refresh:

```csharp
using Rag.NET.DataProviders;

var fixedToken = new StaticTokenProvider("ghp_MyPersonalAccessToken");

var oauth = new OAuthClientCredentialsTokenProvider(
    tokenEndpoint: "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
    clientId:      "my-client-id",
    clientSecret:  "my-client-secret",
    scopes:        ["https://graph.microsoft.com/.default"]);
```

Every connector funnels into one call, with ETag-based skip when a hash store is passed:

```csharp
using Rag.NET.DataProviders;
using Rag.NET.Models;

var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("my-corpus"), hashStore);
Console.WriteLine($"Ingested {result.Ingested}, skipped {result.Skipped}");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
