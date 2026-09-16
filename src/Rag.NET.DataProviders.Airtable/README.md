# Rag.NET.DataProviders.Airtable

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.Airtable.svg?logo=nuget&label=Rag.NET.DataProviders.Airtable)](https://www.nuget.org/packages/Rag.NET.DataProviders.Airtable)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.Airtable.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.Airtable)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Airtable connector for Rag.NET ingestion: rows (and their attachments) from one table
become documents, authenticated with a personal access token and filtered incrementally
on the Last Modified field via `filterByFormula`.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Airtable
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Airtable;

services.AddAirtableDataProvider(
    baseId:              "appXXXXXXXXXXXXXX",
    tableName:           "My Table",
    personalAccessToken: Environment.GetEnvironmentVariable("AIRTABLE_PAT")!,
    configure: opts =>
    {
        opts.DeltaToken = savedTimestamp;  // ISO 8601; null on first run = full table
    });
```

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("airtable"), hashStore);
Console.WriteLine($"Ingested {result.Ingested} rows, skipped {result.Skipped}");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
