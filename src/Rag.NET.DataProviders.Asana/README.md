# Rag.NET.DataProviders.Asana

Asana connector for Rag.NET ingestion: exports workspace (or single-project) tasks as
HTML, authenticated with a personal access token or OAuth2, resuming incrementally via
the `modified_since` parameter.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Asana
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Asana;

services.AddAsanaDataProvider(
    personalAccessToken: Environment.GetEnvironmentVariable("ASANA_PAT")!,
    workspaceGid:        "1234567890",
    configure: opts =>
    {
        opts.ProjectGid = "9876543210";     // null = all projects in the workspace
        opts.DeltaToken = savedDeltaToken;  // ISO 8601 modified_since; null = full traversal
    });
```

An overload accepts an `ITokenProvider` (for example `OAuthClientCredentialsTokenProvider`
from `Rag.NET.DataProviders`) for OAuth2 flows.

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("asana"), hashStore);
Console.WriteLine($"Ingested {result.Ingested} tasks");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
