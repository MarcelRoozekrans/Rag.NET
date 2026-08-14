# Rag.NET.Security.AspNetCore

ASP.NET Core binding for Rag.NET's RBAC: resolves the caller's roles from the current
request's `ClaimsPrincipal`, so `UseRbac()` chunk filtering follows your existing
authentication without a custom `ICallerContext`.

## Install

```bash
dotnet add package Rag.NET.Security.AspNetCore
```

## Setup

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Security;
using Rag.NET.Security.AspNetCore;

services.AddRagNet(rag => rag.UseRbac());
services.AddRagNetAspNetCoreSecurity();  // ICallerContext ← ClaimsPrincipal role claims
```

## Example

Documents declare who may retrieve them via the `allowed_roles` tag at ingestion; from
then on, retrieval inside an authenticated request only returns chunks the caller's role
claims allow:

```csharp
using Rag.NET.Models;

await pipeline.IngestAsync(stream, new DocumentMetadata
{
    DocumentId = new DocumentId("hr-handbook-2024"),
    FileName   = "hr-handbook-2024.pdf",
    Tags = new Dictionary<string, MetadataValue>
    {
        ["allowed_roles"] = "hr,finance",
    },
});
```

## Full guide

- [Security](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/security.md)
