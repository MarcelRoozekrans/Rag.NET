# MCP Server Design

**Date:** 2026-03-13

## Overview

Expose Rag.NET as a [Model Context Protocol](https://modelcontextprotocol.io/) server so any MCP-compatible LLM host (Claude Desktop, Cursor, LM Studio, etc.) can use it as a knowledge base without custom integration code. The design supports two deployment patterns via separate packages: an in-process MCP server and a proxy MCP server that calls a shared Rag.NET backend.

---

## Package Structure

| Package | Role |
|---|---|
| `Rag.NET.Mcp` | Core MCP tools (`retrieve`, `ask`, `ingest`), resolves `IRagPipeline` from DI |
| `Rag.NET.Api` | ASP.NET Core REST API exposing `IRagPipeline` as HTTP endpoints |
| `Rag.NET.Api.Client` | `IRagPipeline` implementation that calls `Rag.NET.Api` over HTTP |
| `Rag.NET.Api.Grpc` | gRPC service exposing `IRagPipeline` |
| `Rag.NET.Api.Grpc.Client` | `IRagPipeline` implementation that calls `Rag.NET.Api.Grpc` over gRPC |
| `Rag.NET.Mcp.Tool` | dotnet global tool — self-contained executable, in-process or proxy mode |

`Rag.NET.Mcp` is always agnostic to whether `IRagPipeline` is in-process or remote.

---

## Deployment Patterns

### Pattern A — In-Process

The host app is both the MCP server and the RAG pipeline.

```
Claude Desktop / Cursor
        ↓ (stdio or HTTP/SSE)
   YourApp: Rag.NET + Rag.NET.Mcp
        ↓
   IRagPipeline (in-process)
        ↓
   Vector Store + Embedding Provider + Chat Client
```

```csharp
builder.Services.AddRagNet(...);
builder.Services
    .AddRagNetMcpServer()
    .WithStdioTransport()
    .WithHttpTransport(5050)
    .WithApiKey("secret");
```

### Pattern B — Shared Backend (REST)

Multiple MCP server instances proxy to a single Rag.NET REST backend.

```
Claude Desktop ──┐
Cursor           ├──→ McpServer: Rag.NET.Mcp + Rag.NET.Api.Client
LM Studio ───────┘            ↓ (HTTP + X-Api-Key)
                     BackendApp: Rag.NET + Rag.NET.Api
```

```csharp
// Backend app
builder.Services.AddRagNet(...);
builder.Services.AddRagNetApi(options => {
    options.ApiKeys = ["key-for-mcp-1", "key-for-mcp-2"];
    options.RoutePrefix = "/rag";
});
app.MapRagNetApi();

// MCP proxy app
builder.Services.AddRagNetApiClient(options => {
    options.BaseUrl = "https://rag-backend.internal";
    options.ApiKey  = "key-for-mcp-1";
});
builder.Services.AddRagNetMcpServer()
    .WithStdioTransport()
    .WithHttpTransport(5050)
    .WithApiKey("secret");
```

### Pattern C — Shared Backend (gRPC)

Same as Pattern B but using gRPC for the MCP server → backend hop. Preferred for internal service-to-service communication: strongly typed, lower overhead, and native server-streaming for `AskStreamingAsync`.

```csharp
// Backend app
builder.Services.AddRagNet(...);
builder.Services.AddRagNetGrpcApi(options => {
    options.ApiKeys = ["key-for-mcp-1"];
});
app.MapRagNetGrpcApi();

// MCP proxy app
builder.Services.AddRagNetGrpcClient(options => {
    options.BaseUrl = "https://rag-backend.internal:5001";
    options.ApiKey  = "key-for-mcp-1";
});
builder.Services.AddRagNetMcpServer()
    .WithStdioTransport()
    .WithHttpTransport(5050)
    .WithApiKey("secret");
```

### Pattern D — dotnet Global Tool (no code required)

```bash
dotnet tool install -g Rag.NET.Mcp.Tool

# In-process, stdio transport (Claude Desktop subprocess)
ragnet-mcp --transport stdio

# In-process, HTTP/SSE transport
ragnet-mcp --transport http --port 5050

# Proxy to REST backend
ragnet-mcp --transport stdio --backend rest --backend-url https://rag.internal --backend-api-key xxx

# Proxy to gRPC backend
ragnet-mcp --transport http --port 5050 --backend grpc --backend-url https://rag.internal:5001 --backend-api-key xxx
```

Configuration is read from `appsettings.json` and environment variables. CLI args override config file values.

---

## REST API (`Rag.NET.Api`)

### Endpoints

| Method | Path | Maps to |
|---|---|---|
| `POST` | `/rag/ingest` | `IngestAsync` |
| `POST` | `/rag/retrieve` | `RetrieveAsync` |
| `POST` | `/rag/ask` | `AskAsync` |
| `GET` | `/rag/ask/stream?query=...` | `AskStreamingAsync` (SSE) |
| `DELETE` | `/rag/documents/{id}` | `DeleteAsync` |

### Auth

API key via `X-Api-Key` header. `RagApi:ApiKeys` in `appsettings.json` accepts an array for key rotation and per-client revocation. Requests without a valid key return `401 Unauthorized`.

---

## gRPC Service (`Rag.NET.Api.Grpc`)

### Proto

```protobuf
service RagService {
  rpc Ingest    (IngestRequest)    returns (IngestResponse);
  rpc Retrieve  (RetrieveRequest)  returns (RetrieveResponse);
  rpc Ask       (AskRequest)       returns (AskResponse);
  rpc AskStream (AskRequest)       returns (stream AskStreamUpdate);
  rpc Delete    (DeleteRequest)    returns (DeleteResponse);
}
```

`AskStream` maps directly to `AskStreamingAsync` via gRPC server-streaming — no SSE workaround needed.

### Auth

API key in gRPC metadata (`x-api-key`), validated by a server-side `Interceptor`. Same multi-key array pattern as REST.

---

## MCP Tools (`Rag.NET.Mcp`)

### `retrieve`

Search the knowledge base for relevant documents.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `query` | string | yes | — | The search query |
| `top_k` | int | no | 5 | Maximum number of results |
| `use_hybrid` | bool | no | true | Enable BM25 + vector hybrid search |

### `ask`

Ask a question and get an answer grounded in the knowledge base.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `query` | string | yes | — | The question |
| `top_k` | int | no | 5 | Chunks to retrieve |
| `use_hybrid` | bool | no | true | Hybrid search |
| `streaming` | bool | no | false | Stream the response |

### `ingest`

Add a document to the knowledge base.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `content` | string | yes | — | Document text (inline) |
| `document_id` | string | no | auto | Stable ID for updates/deletes |
| `file_name` | string | no | — | e.g. `"report.md"`, used to infer content type |
| `content_type` | string | no | — | MIME type override |
| `tags` | string[] | no | — | Metadata tags for filtering |

---

## Error Handling

- MCP tool errors surface as MCP `error` responses with a human-readable message. No stack traces exposed to the LLM.
- `ingest` returns `{ document_id, chunks_stored }` on success.
- REST uses standard HTTP status codes (`400`, `401`, `500`).
- gRPC uses standard status codes (`INVALID_ARGUMENT`, `UNAUTHENTICATED`, `INTERNAL`).

---

## Testing

| Layer | Approach |
|---|---|
| `Rag.NET.Mcp` tools | Unit tests with `IRagPipeline` mock |
| `Rag.NET.Api` REST | `WebApplicationFactory` integration tests |
| `Rag.NET.Api.Grpc` | `GrpcChannel` against in-process `WebApplicationFactory` |
| `Rag.NET.Api.Client` + `Grpc.Client` | Integration tests against respective server |
| End-to-end | dotnet tool as subprocess via stdio, full pipeline with in-memory vector store |

---

## Implementation Notes

The following items appear in this design but were intentionally omitted from v1:

- **`streaming` parameter on `rag_ask`** — Not implemented. The MCP SDK's streaming support operates at the transport level and is separate from tool return values; tool responses are always complete strings. Streaming responses via `AskStreamingAsync` are available through direct SDK integration and are tracked as future work.
- **`--backend rest/grpc` CLI flags on the dotnet global tool** — Not implemented in v1. Users configure the backend by extending `Program.cs`; the available extension points (`AddRagNetApiClient`, `AddRagNetGrpcClient`) are documented via inline comments in the generated tool host.
