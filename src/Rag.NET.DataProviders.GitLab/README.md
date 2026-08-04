# Rag.NET.DataProviders.GitLab

GitLab repository connector for Rag.NET ingestion, built on NGitLab: streams a branch's
files into the pipeline, using the compare API against the last ingested commit SHA so
incremental runs fetch only changed files.

## Install

```bash
dotnet add package Rag.NET.DataProviders.GitLab
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.GitLab;

services.AddGitLabDataProvider(
    baseUrl:         "https://gitlab.com",   // or your self-hosted instance
    projectIdOrPath: "my-org/my-repo",
    token:           Environment.GetEnvironmentVariable("GITLAB_TOKEN")!,
    configure: opts =>
    {
        opts.Branch     = "main";
        opts.Extensions = [".md", ".cs"];
        opts.DeltaToken = savedCommitSha;  // null on first run = full traversal
    });
```

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("gitlab"), hashStore);
Console.WriteLine($"Ingested {result.Ingested}, skipped {result.Skipped}");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
