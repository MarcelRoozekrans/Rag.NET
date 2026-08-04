# Rag.NET.DataProviders.GitHub

GitHub repository connector for Rag.NET ingestion, built on Octokit: streams a branch's
files into the pipeline, with commit-SHA deltas so subsequent runs only fetch what
changed — and the blob SHA as ETag, guaranteeing byte-identical content is never
re-ingested.

## Install

```bash
dotnet add package Rag.NET.DataProviders.GitHub
```

## Setup

This connector is constructed directly (no DI extension method) so you control the
Octokit client:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Octokit;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders.GitHub;

var gitHubClient = new GitHubClient(new ProductHeaderValue("my-app"))
{
    Credentials = new Credentials(Environment.GetEnvironmentVariable("GITHUB_TOKEN")!),
};

var provider = new GitHubDataProvider(
    owner:  "my-org",
    repo:   "my-repo",
    client: gitHubClient,
    options: new GitHubDataProviderOptions
    {
        Branch                = "main",
        Extensions            = [".md", ".cs"],
        LastIngestedCommitSha = savedCommitSha, // null on first run = full traversal
    });

services.AddSingleton<IFileContentProvider>(provider);
```

## Example

```csharp
using Rag.NET.DataProviders;
using Rag.NET.Models;

var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("github"), hashStore);
Console.WriteLine($"Ingested {result.Ingested}, skipped {result.Skipped}");
// Persist the branch HEAD SHA after an error-free run for the next delta.
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
