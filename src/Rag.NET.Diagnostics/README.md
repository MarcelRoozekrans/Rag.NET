# Rag.NET.Diagnostics

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Diagnostics.svg?logo=nuget&label=Rag.NET.Diagnostics)](https://www.nuget.org/packages/Rag.NET.Diagnostics)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Diagnostics.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Diagnostics)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Disposable in-memory pipeline traces for Rag.NET: the last N query executions with chunk
scores, per-stage latencies and guard actions — the "why did it answer that?" tool, with
text capture off by default so nothing sensitive is retained by accident.

## Install

```bash
dotnet add package Rag.NET.Diagnostics
```

## Setup

```csharp
using Rag.NET.Diagnostics;
using Rag.NET.DependencyInjection;

services.AddRagNet(rag =>
{
    // ...the rest of your pipeline first...
    rag.AddRagDiagnostics();  // last, so it observes the guards registered above
});
```

## Example

Read traces back from the `ITraceStore`, newest first:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Diagnostics;

var store = provider.GetRequiredService<ITraceStore>();

foreach (RagTrace trace in store.Snapshot())
{
    Console.WriteLine($"{trace.TraceId} query {trace.QueryHash[..8]}");

    foreach (TraceStage stage in trace.Stages)
        Console.WriteLine($"  {stage.Name,-16} {stage.Duration.TotalMilliseconds:F1} ms");

    foreach (TraceChunk chunk in trace.Chunks)
        Console.WriteLine($"  {chunk.DocumentId}#{chunk.ChunkIndex} scored {chunk.Score:F3}");
}
```

Opt into captured text (query, chunks, prompt, answer) via
`AddRagDiagnostics(o => o.CaptureQueryText = true)` and friends; expose traces over HTTP
with `Rag.NET.Diagnostics.AspNetCore`.

## Full guide

- [Diagnostics](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/diagnostics.md)
