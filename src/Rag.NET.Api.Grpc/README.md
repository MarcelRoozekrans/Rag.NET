# Rag.NET.Api.Grpc

gRPC service for a Rag.NET pipeline: `MapRagNetGrpcApi()` exposes ingest, retrieve, ask
(including server-streamed answers) and delete as a gRPC service with per-call API-key
authentication.

## Install

```bash
dotnet add package Rag.NET.Api.Grpc
```

## Setup

```csharp
using Rag.NET.Api.Grpc.DependencyInjection;
using Rag.NET.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRagNet();  // configure your pipeline as usual
builder.Services.AddRagNetGrpcApi(o => o.ApiKeys = ["your-api-key"]);

var app = builder.Build();

app.MapRagNetGrpcApi();

app.Run();
```

Authentication is an explicit decision: `AddRagNetGrpcApi` throws at startup when
`RagGrpcApiOptions.ApiKeys` is empty, unless you opt out deliberately with
`o.AllowAnonymous = true` (for example behind a trusted gateway that authenticates
upstream). Setting both at once is rejected as a contradiction. The interceptor also
fails closed at call time — if the options end up with no keys and no opt-out, calls
are refused with `Unauthenticated` rather than an accidentally open service.

## Example

Any gRPC client can call the service; pair it with the `Rag.NET.Api.Grpc.Client` package
on the consuming side to keep both ends typed against the same contract. From the command
line:

```bash
grpcurl -H "x-api-key: your-api-key" \
  -d '{"query": "What changed in the Q4 report?"}' \
  rag-backend.internal:5001 ragnet.RagService/Ask
```

## Full guide

- [Hosting patterns (gRPC proxy)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/mcp.mdx)
