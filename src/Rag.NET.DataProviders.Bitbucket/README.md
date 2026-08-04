# Rag.NET.DataProviders.Bitbucket

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
