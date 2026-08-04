# Rag.NET.DataProviders.GoogleDrive

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
