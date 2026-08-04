# Rag.NET.DataProviders.Dropbox

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
