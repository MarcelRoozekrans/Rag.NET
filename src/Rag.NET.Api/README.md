# Rag.NET.Api

ASP.NET Core REST endpoints for a Rag.NET pipeline: `MapRagNetApi()` exposes ingest,
retrieve, ask and delete over HTTP with API-key authentication, and `MapRagNetWebhooks()`
adds an HMAC-verified ingestion webhook.

## Install

```bash
dotnet add package Rag.NET.Api
```

## Setup

```csharp
using Rag.NET.Api.DependencyInjection;
using Rag.NET.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRagNet();  // configure your pipeline as usual
builder.Services.AddRagNetApi(o => o.ApiKeys = ["your-api-key"]);

var app = builder.Build();

app.UseRagNetApiAuthentication(); // X-Api-Key middleware
app.MapRagNetApi();

app.Run();
```

## Example

Event-driven ingestion via the webhook endpoint — callers authenticate with an
HMAC-SHA256 signature over the raw request body instead of the API key:

```csharp
builder.Services.AddRagNetWebhooks(o =>
{
    o.Secret = builder.Configuration["Webhooks:Secret"]!; // required, non-empty
});

app.UseRagNetApiAuthentication();
app.MapRagNetWebhooks(); // POST /rag/webhooks/ingest
```

```bash
curl -X POST https://localhost:5001/rag/webhooks/ingest \
  -H "X-Signature-256: sha256=<hex hmac of body>" \
  -d '{"documentId":"doc-1","content":"full document text"}'
```

## Full guide

- [Event-driven ingestion](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
- [MCP and API hosting patterns](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/mcp.mdx)
