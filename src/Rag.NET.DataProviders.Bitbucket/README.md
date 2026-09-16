# Rag.NET.DataProviders.Bitbucket

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.Bitbucket.svg?logo=nuget&label=Rag.NET.DataProviders.Bitbucket)](https://www.nuget.org/packages/Rag.NET.DataProviders.Bitbucket)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.Bitbucket.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.Bitbucket)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Bitbucket Cloud repository connector for Rag.NET ingestion: streams a branch's files via
the REST API with app-password auth, using the diffstat API against the last ingested
commit hash for incremental runs.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Bitbucket
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Bitbucket;

services.AddBitbucketDataProvider(
    workspace:   "my-workspace",
    repoSlug:    "my-repo",
    username:    "my-username",
    appPassword: Environment.GetEnvironmentVariable("BITBUCKET_APP_PASSWORD")!,
    configure: opts =>
    {
        opts.Ref        = "main";           // branch, tag or commit ref
        opts.Extensions = [".md", ".cs"];
        opts.DeltaToken = savedCommitHash;  // null on first run = full traversal
    });
```

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("bitbucket"), hashStore);
Console.WriteLine($"Ingested {result.Ingested}, skipped {result.Skipped}");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
