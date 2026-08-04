# Rag.NET.Api.Client

Typed HTTP client for a remote `Rag.NET.Api` host: registers an `IRagPipeline`
implementation that forwards every call over HTTP, so application code cannot tell whether
the pipeline runs in-process or behind a REST API.

## Install

```bash
dotnet add package Rag.NET.Api.Client
```

## Setup

```csharp
using Rag.NET.Api.Client.DependencyInjection;

services.AddRagNetApiClient(o =>
{
    o.BaseUrl = "https://rag-backend.internal";
    o.ApiKey  = "your-api-key";
});
```

## Example

The registered pipeline is the same `IRagPipeline` abstraction the in-process pipeline
implements — swap the backend without touching call sites:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

var pipeline = provider.GetRequiredService<IRagPipeline>();

var response = await pipeline.AskAsync("What changed in the Q4 report?");
Console.WriteLine(response.Answer);
```

## Full guide

- [Hosting patterns (REST proxy)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/mcp.mdx)
