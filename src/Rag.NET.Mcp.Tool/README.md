# Rag.NET.Mcp.Tool

A self-contained Model Context Protocol server for Rag.NET, packaged as a .NET global tool
(`ragnet-mcp`) — run a RAG-backed MCP server from configuration alone, no C# project
required.

## Install

```bash
dotnet tool install -g Rag.NET.Mcp.Tool
```

## Setup

The tool reads its pipeline configuration (embedding provider, vector store, chat client)
from an `appsettings.json` next to the working directory, with environment variables
overriding individual values — the standard .NET configuration layering.

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
