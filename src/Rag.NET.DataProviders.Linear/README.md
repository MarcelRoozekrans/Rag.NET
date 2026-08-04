# Rag.NET.DataProviders.Linear

Linear connector for Rag.NET ingestion: issues and their comments are fetched over
Linear's GraphQL API and emitted as Markdown (`{identifier} {title}.md`), resuming
incrementally from an `updatedAt` watermark.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Linear
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Linear;

services.AddLinearDataProvider(
    apiKey: Environment.GetEnvironmentVariable("LINEAR_API_KEY")!, // lin_api_..., bare header
    configure: opts =>
    {
        opts.TeamKeys   = ["ENG", "OPS"];           // null = all teams
        opts.States     = ["started", "completed"]; // workflow state *types*; null = all
        opts.DeltaToken = savedDeltaToken;          // updatedAt watermark; null = full traversal
    });
```

Personal API keys are sent as a bare `Authorization` header — no `Bearer` prefix.

## Example

The watermark only advances after a complete traversal; a run that failed mid-pagination
returns `null`, so keep the previous token in that case:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders.Linear;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = (LinearDataProvider)sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("linear"), hashStore);

if (result.Errors.Count == 0 && provider.GetDeltaToken() is { } token)
    SaveDeltaToken(token);
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
