# Rag.NET.Mcp.Tool

A self-contained Model Context Protocol server for Rag.NET, packaged as a .NET global tool
(`ragnet-mcp`) — run a RAG-backed MCP server from configuration alone, no C# project
required.

## Install

```bash
dotnet tool install -g Rag.NET.Mcp.Tool
```

## Configure

The tool wires its pipeline — chat client, embedding generator, vector store — from an
`appsettings.json` next to its working directory, or from environment variables (using `__`
as the section separator, e.g. `RagNet__ChatClient__ApiKey`) — the standard .NET
configuration layering `WebApplication.CreateBuilder(args)` already provides. A sample
`appsettings.sample.json`, showing every supported `VectorStore:Kind`, ships alongside the
installed binaries; copy it to `appsettings.json` and fill in real values.

```json
{
  "RagNet": {
    "ChatClient": {
      "Endpoint": "https://api.openai.com/v1",
      "ApiKey": "…",
      "Model": "gpt-4o-mini"
    },
    "Embeddings": {
      "Endpoint": "https://api.openai.com/v1",
      "ApiKey": "…",
      "Model": "text-embedding-3-small",
      "VectorDimensions": 1536
    },
    "VectorStore": {
      "Kind": "InMemory | Qdrant | PgVector",
      "Qdrant": { "Host": "…", "Port": 6334, "CollectionName": "…" },
      "PgVector": { "ConnectionString": "…" }
    }
  }
}
```

The chat client and embedding generator both go through one OpenAI-compatible endpoint —
that covers OpenAI, Azure OpenAI, OpenRouter, Ollama, and LM Studio, since they all speak the
same wire API. The vector store is one of three kinds: `InMemory` (the default, zero setup,
but **every ingested document is lost when the process exits** — a warning is logged at
startup), `Qdrant`, or `PgVector`.

A misconfigured setting — an unrecognised `Kind`, a missing `Endpoint`/`Model`, an absent or
non-positive `VectorDimensions` — fails at startup with a message naming both the setting and
the configuration key that fixes it, rather than failing the first time an MCP client calls a
tool.

Need a provider outside that set — Weaviate, Pinecone, Chroma, Azure AI Search, ONNX
embeddings, or a bespoke `IChatClient`? Host the `Rag.NET.Mcp` library directly in your own
application and register whatever you like; this tool covers the bounded, OpenAI-compatible
set above and nothing wider.

## Run

```bash
# stdio transport (Claude Desktop subprocess)
ragnet-mcp

# HTTP/SSE on port 5050 with API-key auth
ragnet-mcp --transport http --port 5050 --api-key your-secret
```

Claude Desktop configuration for the stdio variant:

```json
{
  "mcpServers": {
    "ragnet": { "command": "ragnet-mcp" }
  }
}
```

The server exposes the `rag_retrieve`, `rag_ask` and `rag_ingest` tools to the MCP host.
Hosting the server inside your own application instead? Use the `Rag.NET.Mcp` library
package.

## Full guide

- [MCP server](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/mcp.mdx)
