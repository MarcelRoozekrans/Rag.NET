# Rag.NET.DataProviders.GoogleDrive

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.GoogleDrive.svg?logo=nuget&label=Rag.NET.DataProviders.GoogleDrive)](https://www.nuget.org/packages/Rag.NET.DataProviders.GoogleDrive)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.GoogleDrive.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.GoogleDrive)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Google Drive connector for Rag.NET ingestion: enumerates a whole drive or one folder
recursively with a service account, resuming incrementally from a Changes.List page
token.

## Install

```bash
dotnet add package Rag.NET.DataProviders.GoogleDrive
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.GoogleDrive;

services.AddGoogleDriveDataProvider(
    serviceAccountKeyPath: "/secrets/service-account.json",
    configure: opts =>
    {
        opts.FolderId   = "1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs74OgVE2upms"; // null = entire drive
        opts.Extensions = [".pdf", ".docx"];
        opts.DeltaToken = savedPageToken;  // null on first run = full traversal
    });
```

An overload accepts a pre-built `DriveService` when your application already manages
Google credentials. Share the target folder with the service account's email address.

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("gdrive"), hashStore);
Console.WriteLine($"Ingested {result.Ingested}, skipped {result.Skipped}");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
