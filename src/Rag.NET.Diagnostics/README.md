# Rag.NET.Diagnostics

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
