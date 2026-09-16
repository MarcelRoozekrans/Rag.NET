# Rag.NET.DataProviders.Jira

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.Jira.svg?logo=nuget&label=Rag.NET.DataProviders.Jira)](https://www.nuget.org/packages/Rag.NET.DataProviders.Jira)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.Jira.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.Jira)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Jira Cloud connector for Rag.NET ingestion: exports issues as HTML, scoped by JQL,
authenticated with email + API token, with an `updated >` timestamp watermark for
incremental runs.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Jira
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Jira;

services.AddJiraDataProvider(
    baseUrl:  "https://your-domain.atlassian.net",
    email:    "user@example.com",
    apiToken: Environment.GetEnvironmentVariable("JIRA_API_TOKEN")!,
    configure: opts =>
    {
        opts.Jql        = "project = ENG";  // null = all issues
        opts.DeltaToken = savedDeltaToken;  // null on first run = full traversal
    });
```

Issues arrive as HTML — register `AddHtmlParser()` from `Rag.NET.Parsers.Html` in your
pipeline.

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("jira"), hashStore);

foreach (var error in result.Errors)
    Console.WriteLine(error); // HTTP failures are collected, not thrown — the run continues
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
