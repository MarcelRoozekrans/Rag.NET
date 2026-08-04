# Rag.NET.DataProviders.Jira

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
