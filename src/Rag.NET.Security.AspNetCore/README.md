# Rag.NET.Security.AspNetCore

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Security.AspNetCore.svg?logo=nuget&label=Rag.NET.Security.AspNetCore)](https://www.nuget.org/packages/Rag.NET.Security.AspNetCore)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Security.AspNetCore.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Security.AspNetCore)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

ASP.NET Core binding for Rag.NET's RBAC: resolves the caller's roles from the current
request's `ClaimsPrincipal`, so `UseRbac()` chunk filtering follows your existing
authentication without a custom `ICallerContext`.

## Install

```bash
dotnet add package Rag.NET.Security.AspNetCore
```

## Setup

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Security;
using Rag.NET.Security.AspNetCore;

services.AddRagNet(rag => rag.UseRbac());
services.AddRagNetAspNetCoreSecurity();  // ICallerContext ← ClaimsPrincipal role claims
```

## Example

Documents declare who may retrieve them via the `allowed_roles` tag at ingestion; from
then on, retrieval inside an authenticated request only returns chunks the caller's role
claims allow:

```csharp
using Rag.NET.Models;

await pipeline.IngestAsync(stream, new DocumentMetadata
{
    DocumentId = new DocumentId("hr-handbook-2024"),
    FileName   = "hr-handbook-2024.pdf",
    Tags = new Dictionary<string, MetadataValue>
    {
        ["allowed_roles"] = "hr,finance",
    },
});
```

## Full guide

- [Security](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/security.md)
