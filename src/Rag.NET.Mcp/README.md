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

For multiple concurrent clients, serve HTTP/SSE instead of stdio. `Rag.NET.Mcp` deliberately does
not reference `ModelContextProtocol.AspNetCore` — taking that dependency would force ASP.NET Core
on every consumer hosting MCP tools in a non-web process — so HTTP transport is configured through
the `IMcpServerBuilder` the MCP SDK itself returns, exposed here as `McpServerBuilder.Server`.

Add `ModelContextProtocol.AspNetCore` to your own project, then call the SDK's own
`WithHttpTransport()` on `.Server`, map its endpoints, and supply any authentication as ordinary
ASP.NET Core middleware — this package wires the transport and nothing else.

**No example is shown here on purpose.** Those APIs belong to ASP.NET Core and the SDK's ASP.NET
package, neither of which this package ships or depends on, so an example using them could not be
checked against what you actually install — and documentation that references APIs a package does
not have is this repository's most-repeated defect. A complete, working HTTP host lives in
[`docs/guide/mcp.mdx`](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/mcp.mdx),
and `Rag.NET.Mcp.Tool` is a runnable one.

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
