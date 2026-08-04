# Rag.NET.DataProviders.Airtable

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
