# Rag.NET.DataProviders.Asana

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.Asana.svg?logo=nuget&label=Rag.NET.DataProviders.Asana)](https://www.nuget.org/packages/Rag.NET.DataProviders.Asana)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.Asana.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.Asana)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
