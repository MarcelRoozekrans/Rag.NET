# Rag.NET.Security

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Security.svg?logo=nuget&label=Rag.NET.Security)](https://www.nuget.org/packages/Rag.NET.Security)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Security.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Security)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Prompt-injection defence in depth for Rag.NET: chunk and query sanitisers, retrieval
guards, trust-level enforcement, prompt hardening, role-based chunk access (RBAC) and
PII redaction — each an explicit opt-in layer around the pipeline.

## Install

```bash
dotnet add package Rag.NET.Security
```

## Setup

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Security;

services.AddRagNet(rag => rag
    .UseChunkSanitiser()     // strip injection patterns from ingested text
    .UseQuerySanitiser()     // and from incoming questions
    .UsePromptHardening());  // delimit untrusted context in the prompt
```

## Example

PII redaction runs at ingestion, with the regex pattern set open for editing:

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Security;

services.AddRagNet(rag => rag
    .UsePiiDetection(o =>
    {
        o.Patterns.Remove(PiiPatterns.Ssn);   // drop a built-in pattern

        o.Patterns.Add(new PiiPattern          // add your own — Dutch BSN
        {
            Placeholder  = "[BSN]",
            RegexPattern = @"\b\d{9}\b",
        });
    }));
```

RBAC filters retrieved chunks by the caller's roles against each document's
`allowed_roles` tag: `rag.UseRbac()` plus an `ICallerContext` implementation — or the
ready-made `ClaimsPrincipal` binding in `Rag.NET.Security.AspNetCore`.

## Full guide

- [Security](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/security.md)
