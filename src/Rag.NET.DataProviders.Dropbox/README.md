# Rag.NET.DataProviders.Dropbox

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.Dropbox.svg?logo=nuget&label=Rag.NET.DataProviders.Dropbox)](https://www.nuget.org/packages/Rag.NET.DataProviders.Dropbox)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.Dropbox.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.Dropbox)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Dropbox connector for Rag.NET ingestion: enumerates a folder tree via the official
Dropbox SDK and resumes incrementally from a ListFolder cursor — Dropbox cursors do not
expire, so the delta token is safe to store indefinitely.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Dropbox
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Dropbox;

services.AddDropboxDataProvider(
    accessToken: Environment.GetEnvironmentVariable("DROPBOX_ACCESS_TOKEN")!,
    configure: opts =>
    {
        opts.FolderPath = "/Engineering/Docs"; // "" = root
        opts.DeltaToken = savedCursor;         // null on first run = full traversal
    });
```

An overload accepts an `ITokenProvider` (for example `OAuthClientCredentialsTokenProvider`
from `Rag.NET.DataProviders`) for OAuth refresh-token flows.

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("dropbox"), hashStore);
Console.WriteLine($"Ingested {result.Ingested}, skipped {result.Skipped}");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
