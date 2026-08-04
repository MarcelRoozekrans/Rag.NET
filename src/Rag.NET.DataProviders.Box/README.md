# Rag.NET.DataProviders.Box

Box connector for Rag.NET ingestion: enumerates a folder tree via the Box .NET SDK with
JWT server-to-server auth, resuming incrementally from a Box events-stream position.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Box
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Box;

services.AddBoxDataProvider(
    jwtConfigJson: File.ReadAllText("/secrets/box-config.json"),
    configure: opts =>
    {
        opts.RootFolderId = "0";               // "0" = root
        opts.Extensions   = [".pdf", ".docx"];
        opts.DeltaToken   = savedStreamPosition; // null on first run = full traversal
    });
```

An overload accepts a pre-built `BoxClient` when your application already manages Box
authentication.

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("box"), hashStore);
Console.WriteLine($"Ingested {result.Ingested}, skipped {result.Skipped}");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
