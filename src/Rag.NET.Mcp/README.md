# Rag.NET.Mcp

Model Context Protocol server for Rag.NET: `AddRagNetMcpServer()` exposes your pipeline to
MCP hosts (Claude Desktop, IDEs, agents) as the `rag_retrieve`, `rag_ask` and `rag_ingest`
tools, over stdio or HTTP/SSE transports.

## Install

```bash
dotnet add package Rag.NET.Mcp
```

Prefer zero code? `Rag.NET.Mcp.Tool` ships the same server as a `dotnet tool`.

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.NET.DependencyInjection;
using Rag.NET.Mcp.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRagNet();  // configure your pipeline as usual

builder.Services
    .AddRagNetMcpServer()
    .WithStdioTransport();     // subprocess transport for Claude Desktop

using var host = builder.Build();
// Run the host as usual — the MCP server starts and stops with it.
```

## Example

For multiple concurrent clients, serve HTTP/SSE with API-key auth instead of stdio:

```csharp
builder.Services
    .AddRagNetMcpServer()
    .WithHttpTransport(5050)
    .WithApiKey("your-secret");
```

Claude Desktop configuration for the stdio variant:

```json
{
  "mcpServers": {
    "ragnet": { "command": "dotnet", "args": ["run", "--project", "path/to/your/host"] }
  }
}
```

## Full guide

- [MCP server](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/mcp.mdx)
