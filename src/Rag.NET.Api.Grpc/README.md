# Rag.NET.Api.Grpc

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Api.Grpc.svg?logo=nuget&label=Rag.NET.Api.Grpc)](https://www.nuget.org/packages/Rag.NET.Api.Grpc)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Api.Grpc.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Api.Grpc)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
