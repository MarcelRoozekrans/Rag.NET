# Rag.NET.Api.Grpc.Client

gRPC client for a remote `Rag.NET.Api.Grpc` host: registers an `IRagPipeline`
implementation that forwards every call over gRPC — the low-latency counterpart to the
REST-based `Rag.NET.Api.Client`.

## Install

```bash
dotnet add package Rag.NET.Api.Grpc.Client
```

## Setup

```csharp
using Rag.NET.Api.Grpc.Client.DependencyInjection;

services.AddRagNetGrpcClient(o =>
{
    o.BaseUrl = "https://rag-backend.internal:5001";
    o.ApiKey  = "your-api-key";
});
```

## Example

Application code resolves the standard `IRagPipeline`; streaming answers arrive over
gRPC server streaming:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

var pipeline = provider.GetRequiredService<IRagPipeline>();

await foreach (var update in pipeline.AskStreamingAsync("Summarise the incident report"))
{
    if (update.TextDelta is not null)
        Console.Write(update.TextDelta);
}
```

## Full guide

- [Hosting patterns (gRPC proxy)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/mcp.mdx)
