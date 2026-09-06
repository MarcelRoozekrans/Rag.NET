# Rag.NET.Mcp.AspNetCore

ASP.NET Core hosting for the Rag.NET MCP server: an HTTP transport that **refuses to serve an
unauthenticated write surface**.

## Install

```bash
dotnet add package Rag.NET.Mcp.AspNetCore
```

## Setup

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Mcp.AspNetCore;
using Rag.NET.Mcp.DependencyInjection;

// Your pipeline, configured as usual — this package adds nothing to how it is built, and the
// store, embeddings and chunking come from whichever Rag.NET packages you already reference.
builder.Services.AddRagNet();

builder.Services
    .AddRagNetMcpServer()
    .WithRagNetHttpTransport(o => o.ApiKey = "your-secret");

var app = builder.Build();

app.MapRagNetMcp("/mcp");
await app.RunAsync();
```

Clients send the key as `X-Api-Key`.

## Why the API is shaped this way

The MCP tool surface exposes `rag_ingest` as well as retrieve and ask. Over HTTP that makes it a
remote **write** surface: anyone who can reach the port can write into the corpus your answers come
from, and retrieval degrades silently rather than erroring, so nothing surfaces it
([#198](https://github.com/MarcelRoozekrans/Rag.NET/issues/198)).

Two consequences, both deliberate:

**`WithRagNetHttpTransport` throws when you configure neither a key nor an explicit opt-out.**
Leaving both unset is not a default; it is an unmade decision, and the process fails at startup
rather than on the first unauthenticated request. If the host really is behind a gateway that
authenticates for it, say so:

```csharp
.WithRagNetHttpTransport(o => o.AllowAnonymous = true);
```

**`MapRagNetMcp` attaches the key check to the endpoints it maps.** There is no separate middleware
to install, in the wrong order or not at all — the endpoints and their authentication come from the
same call, so "mapped but unauthenticated" is not expressible. The check is scoped to those
endpoints, so a host that mounts MCP beside its own unrelated routes does not authenticate those
with this key as a side effect.

## Why this is a separate package

`Rag.NET.Mcp` references only `ModelContextProtocol`, never `ModelContextProtocol.AspNetCore` —
taking the ASP.NET Core dependency there would force a web framework on every consumer hosting MCP
tools in a non-web process, which stdio consumers do. So the guarded transport lives here, where
only web hosts pay for it.

The same reasoning split `Rag.NET.Security.Audit.Sqlite` out of `Rag.NET.Security`.

## What this does not give you

**Authentication, not authorization, and one shared secret rather than an identity.** Every client
presenting the key is indistinguishable from every other, there is no per-client revocation, and
rotating means changing the key. That is the same scheme `Rag.NET.Api`, its gRPC sibling and
`ragnet-mcp` use; OAuth and per-client identity are not implemented on any of them.

If you need more, `MapRagNetMcp` returns the endpoint convention builder, so ASP.NET Core's own
authorization applies on top:

```csharp
app.MapRagNetMcp("/mcp").RequireAuthorization("McpPolicy");
```

## Stdio

Stdio has no network exposure and needs none of this. Use `Rag.NET.Mcp` directly:

```csharp
builder.Services.AddRagNetMcpServer().WithStdioTransport();
```
