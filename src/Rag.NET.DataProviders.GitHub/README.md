# Rag.NET.DataProviders.GitHub

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.GitHub.svg?logo=nuget&label=Rag.NET.DataProviders.GitHub)](https://www.nuget.org/packages/Rag.NET.DataProviders.GitHub)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.GitHub.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.GitHub)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
