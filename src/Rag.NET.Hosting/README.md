# Rag.NET.Hosting

Configuration-driven pipeline wiring for hosting Rag.NET inside an executable — a `dotnet tool`,
a CLI, a worker service. One extension method binds a `RagNet` configuration section to a working
`IRagPipeline`: an OpenAI-compatible chat client and embedding generator (OpenAI, Azure OpenAI,
OpenRouter, Ollama, and LM Studio all speak the same wire API), plus one of three vector stores —
`InMemory`, `Qdrant`, or `PgVector`. That is a deliberately bounded set: anything outside it —
Weaviate, Pinecone, Chroma, Azure AI Search, ONNX embeddings, a bespoke `IChatClient` — is served
by referencing `Rag.NET.Mcp` or `Rag.NET` directly and registering your own store, which stays a
real answer rather than an apology.

## Install

```bash
dotnet add package Rag.NET.Hosting
```

## Setup

```csharp
using Rag.NET.Hosting.DependencyInjection;

services.AddRagNetPipelineFromConfiguration(configuration);
```

`configuration` is any `IConfiguration` with a `RagNet` section:

```json
{
  "RagNet": {
    "ChatClient":  { "Endpoint": "https://openrouter.ai/api/v1", "ApiKey": "…", "Model": "meta-llama/llama-3.3-70b-instruct" },
    "Embeddings":  { "Endpoint": "https://openrouter.ai/api/v1", "ApiKey": "…", "Model": "text-embedding-3-small", "VectorDimensions": 1536 },
    "VectorStore": {
      "Kind": "InMemory",
      "Qdrant":   { "Host": "localhost", "Port": 6334, "CollectionName": "my-collection" },
      "PgVector": { "ConnectionString": "Host=localhost;Database=ragnet;Username=…;Password=…" }
    }
  }
}
```

A few things about that shape are load-bearing, not arbitrary:

- **`VectorDimensions` lives under `Embeddings`, not the store.** It is a property of the
  embedding model — `nomic-embed-text` is 768, OpenAI's `text-embedding-3-small` is 1536 — and
  every store merely has to agree with it.
- **`Qdrant` and `PgVector` take different settings**, because the builder extensions they wrap
  do: `UseQdrant` wants a host, port, and collection name; `UsePgVector` wants a connection
  string. Only the section matching `VectorStore.Kind` is read.
- **`Kind` defaults to `InMemory`.** Its data does not survive a restart — the same silent-reset
  shape `UseCostBudgeting()`'s default in-memory cost ledger already cost this repository once,
  with real money behind it.

Environment variables come free with the standard `IConfiguration` builder — for example
`RagNet__VectorStore__Kind=Qdrant` overrides the JSON above without touching a file.

No startup validation runs yet: a misconfigured value fails wherever it is first used (`Uri`
construction, store connection, first ingest), not with a diagnostic naming the setting and the
key that fixes it. That is deliberately out of scope for this wiring and lands as follow-on work
in the same phase.
