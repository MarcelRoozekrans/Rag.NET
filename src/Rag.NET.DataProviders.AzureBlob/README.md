# Rag.NET.DataProviders.AzureBlob

Azure Blob Storage connector for Rag.NET ingestion: enumerates a container (optionally
under a prefix) and streams blobs into the pipeline, using each blob's ETag so unchanged
blobs are skipped on re-runs.

## Install

```bash
dotnet add package Rag.NET.DataProviders.AzureBlob
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.AzureBlob;

services.AddAzureBlobDataProvider(
    connectionString: Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")!,
    containerName:    "my-documents",
    configure: opts =>
    {
        opts.Extensions = [".pdf", ".docx", ".md"];  // ["*"] = everything
        opts.Prefix     = "reports/";
    });
```

An overload accepts a `TokenCredential` + container URI for managed identity instead of a
connection string.

## Example

Unlike the cursor-based connectors, Azure Blob has no delta token — pass a hash store and
the pipeline compares each blob's ETag against the stored value:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var hashStore = sp.GetRequiredService<IContentHashStore>();

var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("blob-docs"), hashStore);
Console.WriteLine($"Ingested {result.Ingested}, skipped {result.Skipped} (unchanged ETags)");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
