---
id: api
title: REST and gRPC APIs
sidebar_label: REST and gRPC
sidebar_position: 13
---

# REST and gRPC APIs

`Rag.NET.Api` and `Rag.NET.Api.Grpc` serve a pipeline over the network. Each has a matching
client package — `Rag.NET.Api.Client` and `Rag.NET.Api.Grpc.Client` — that implements
`IRagPipeline` against it, so calling code does not know whether the pipeline is in-process or
three hops away.

That symmetry is the point. A service that takes `IRagPipeline` in its constructor works
unchanged whether you register the real pipeline, the HTTP client, or the gRPC client.

## When you need one

You do not need either for a single application that ingests and queries its own corpus — host
the pipeline in-process and skip this page. Reach for a server when:

- **Several applications share one corpus.** One pipeline owns the vector store and the
  embedding spend; everything else talks to it.
- **The caller is not .NET.** REST and gRPC are the two ways out of the ecosystem.
- **Ingestion and query have different scaling shapes.** Ingestion is bursty and slow, query is
  steady and fast; separating them lets you size them separately.

If the caller is an LLM agent rather than an application, you want the
[MCP server](mcp.mdx) instead — it exposes the same pipeline as tools rather than as endpoints.

## REST

```bash
dotnet add package Rag.NET.Api
```

```csharp
builder.Services.AddRagNet(rag => rag
    .UseQdrant("localhost", 6334, "docs", vectorDimensions: 1536));

builder.Services.AddRagNetApi(o => o.ApiKeys = [builder.Configuration["RagApiKey"]!]);

var app = builder.Build();

app.UseRagNetApiAuthentication();   // must come before MapRagNetApi
app.MapRagNetApi();

app.Run();
```

### Endpoints

| Method | Path | Maps to |
|---|---|---|
| `POST` | `/rag/ingest` | `IngestAsync` |
| `POST` | `/rag/retrieve` | `RetrieveAsync` |
| `POST` | `/rag/ask` | `AskAsync` |
| `GET` | `/rag/ask/stream?query=...` | `AskStreamingAsync`, as server-sent events |
| `DELETE` | `/rag/documents/{documentId}` | `DeleteAsync` |

`RagApiOptions.RoutePrefix` changes the `/rag` prefix.

### Authentication is a decision you have to make

`AddRagNetApi` throws if you have not made it. An empty `ApiKeys` with `AllowAnonymous` left
`false` is the shape that serves every endpoint — including ingest and delete — to anyone who
finds the port, so it is rejected at startup rather than at the first unwanted request:

```csharp
// Throws: neither a key nor an explicit opt-out.
builder.Services.AddRagNetApi();

// Throws: the two contradict each other.
builder.Services.AddRagNetApi(o => { o.ApiKeys = ["k"]; o.AllowAnonymous = true; });

// Fine — keys required.
builder.Services.AddRagNetApi(o => o.ApiKeys = ["k1", "k2"]);

// Fine — anonymous, and you said so.
builder.Services.AddRagNetApi(o => o.AllowAnonymous = true);
```

Configuring keys is not enough on its own. `UseRagNetApiAuthentication()` is what puts the
middleware in the pipeline, and omitting it used to leave the keys configured, unread, and every
endpoint open. `MapRagNetApi` now throws when the `Use` call has not run, which is also why the
`Use` call must come first: an `IEndpointRouteBuilder` cannot inspect the middleware pipeline, so
the check is on the marker that `Use` sets.

### Webhooks

`MapRagNetWebhooks()` adds a signed endpoint that parses an inbound payload with the registered
`IWebhookPayloadParser` and enqueues the resulting jobs on the `IIngestionJobQueue` — the
push half of [event-driven ingestion](ingestion.md#event-driven-ingestion), where a SaaS source
tells you a document changed instead of being polled.

```csharp
builder.Services.AddRagNetWebhooks(o => o.Secret = builder.Configuration["WebhookSecret"]!);
app.MapRagNetWebhooks();
```

The route is deliberately exempt from API-key auth: the HMAC signature over the raw body is the
credential, and the sender has no key to present. It answers `202 Accepted` with
`{ enqueued: n }`, `401` on a missing or invalid signature, `400` on invalid JSON or a rejected
payload, and `503` when no `IIngestionJobQueue` is registered.

### The HTTP client

```bash
dotnet add package Rag.NET.Api.Client
```

```csharp
services.AddRagNetApiClient(o =>
{
    o.BaseUrl = "https://rag.internal:5001";
    o.ApiKey  = configuration["RagApiKey"]!;
});
```

This registers an `IRagPipeline`. Inject it exactly as you would the in-process one.

## gRPC

```bash
dotnet add package Rag.NET.Api.Grpc
```

```csharp
builder.Services.AddRagNet(rag => rag.UseQdrant(/* … */));
builder.Services.AddRagNetGrpcApi(o => o.ApiKeys = [builder.Configuration["RagApiKey"]!]);

var app = builder.Build();
app.MapRagNetGrpcApi();
app.Run();
```

`AddRagNetGrpcApi` enforces the same either-keys-or-explicit-anonymous rule as `AddRagNetApi`,
for the same reason. `MapRagNetGrpcApi()` is the mapping call — the service implementation itself
is internal, so there is no `MapGrpcService<T>()` for you to write.

The service contract is five methods, one per pipeline operation:

```proto
service RagService {
  rpc Ingest    (IngestRequest)   returns (IngestResponse);
  rpc Retrieve  (RetrieveRequest) returns (RetrieveResponse);
  rpc Ask       (AskRequest)      returns (AskResponse);
  rpc AskStream (AskRequest)      returns (stream AskStreamUpdate);
  rpc Delete    (DeleteRequest)   returns (DeleteResponse);
}
```

`rag.proto` lives at `src/Rag.NET.Api.Grpc/Protos/rag.proto` in the repository. It is compiled
into the package rather than shipped alongside it, so a non-.NET caller generating its own client
takes the file from source control.

```bash
dotnet add package Rag.NET.Api.Grpc.Client
```

```csharp
services.AddRagNetGrpcClient(o =>
{
    o.BaseUrl = "https://rag.internal:5002";
    o.ApiKey  = configuration["RagApiKey"]!;
});
```

The key travels as call credentials on every request.

## Choosing between them

| | REST | gRPC |
|---|---|---|
| Non-.NET callers | Any HTTP client | Needs a generated client from `rag.proto` |
| Streaming answers | Server-sent events over `GET` | A native server-streaming call |
| Payload | JSON | Protobuf — smaller and faster to parse |
| Debugging | `curl`, browser dev tools | `grpcurl`, or a proto-aware proxy |
| Metadata fidelity | Typed JSON | `map<string,string>` — [typed chunk metadata](ingestion.md#typed-metadata-values) is flattened to strings on the wire |

REST unless you have a reason. The metadata row is the one that bites: a filter that depends on a
numeric or date-typed metadata value is comparing strings by the time it crosses gRPC.

## What neither gives you

Both expose the pipeline, not the corpus. There is no endpoint that lists documents, reports
index size or replays a query's stages — that is [the diagnostics
package](diagnostics.md) and its own opt-in endpoint. And neither carries
[security](security.md) with it: an API key says the caller is allowed to reach the pipeline, not
which chunks they are allowed to see. If chunks are access-controlled, `Rag.NET.Security`'s RBAC
and the `ICallerContext` binding in `Rag.NET.Security.AspNetCore` are what enforce that, and they
are a separate decision from this one.
