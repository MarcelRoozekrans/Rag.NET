# Rag.NET.Diagnostics.AspNetCore

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Diagnostics.AspNetCore.svg?logo=nuget&label=Rag.NET.Diagnostics.AspNetCore)](https://www.nuget.org/packages/Rag.NET.Diagnostics.AspNetCore)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Diagnostics.AspNetCore.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Diagnostics.AspNetCore)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

HTTP endpoints for Rag.NET pipeline traces: `MapRagNetTrace()` serves the trace store
captured by `Rag.NET.Diagnostics` — explicitly mapped, never automatic, so traces are
only exposed where you decide they are.

## Install

```bash
dotnet add package Rag.NET.Diagnostics.AspNetCore
```

## Setup

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Diagnostics;
using Rag.NET.Diagnostics.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRagNet(rag => rag.AddRagDiagnostics());

var app = builder.Build();

// GET /ragnet/traces           — a summary per retained trace, newest first
// GET /ragnet/traces/{traceId} — the whole trace, captured text included
app.MapRagNetTrace();

app.Run();
```

## Example

The route prefix is yours to move, and the endpoints deliberately ship without built-in
auth — put them behind whatever protects the rest of your operational surface (for
example `UseRagNetApiAuthentication()` from `Rag.NET.Api`):

```csharp
app.MapRagNetTrace("/internal/ragnet/traces");
```

## Full guide

- [Diagnostics](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/diagnostics.md)
