# Rag.NET.Diagnostics.AspNetCore

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
