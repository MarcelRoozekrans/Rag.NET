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
not reference `ModelContextProtocol.AspNetCore` — taking that dependency would force it on every
consumer hosting MCP tools in a non-web process — so HTTP transport is configured through the
`IMcpServerBuilder` the MCP SDK itself returns, exposed here as `McpServerBuilder.Server`. Add
`ModelContextProtocol.AspNetCore` to your own project to reach it:

```csharp
builder.Services
    .AddRagNetMcpServer()
    .Server
    .WithHttpTransport();

var app = builder.Build();

// API-key auth is your own middleware; Rag.NET.Mcp only wires the transport.
app.Use(async (context, next) =>
{
    if (context.Request.Headers["X-Api-Key"] != "your-secret")
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context);
});

app.MapMcp("/mcp");
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
