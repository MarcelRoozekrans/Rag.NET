---
id: security-layer-design
title: Security Layer Design — RBAC, PII Detection, Audit Log
sidebar_position: 1
---

# Security Layer Design — RBAC, PII Detection, Audit Log

## Overview

Three independent, composable security features added to `Rag.NET.Security`. Each registers via its own `RagBuilder` extension and can be enabled individually. All follow the existing pattern of standalone abstractions + independent behaviors/decorators.

---

## Approach

Option A — three independent behaviors/decorators. Each is a standalone pipeline extension that fits the existing `IRetrievalBehavior` / `IChunkSanitiser` patterns. No shared security context required. Features are orthogonal: RBAC, PII detection, and audit log can be enabled in any combination.

---

## Feature 1 — RBAC on Chunks

### Abstraction

New `ICallerContext` interface in `Rag.NET.Security`. Scoped lifetime — a fresh instance per request. When not registered, RBAC is pass-through.

```csharp
public interface ICallerContext
{
    IReadOnlyList<string> GetRoles();
}
```

### At ingest

Callers stamp `allowed_roles` into `DocumentMetadata.Tags`:

```csharp
new DocumentMetadata
{
    Tags = { ["allowed_roles"] = "hr,finance" }   // comma-separated
}
```

This propagates to `chunk.Metadata["allowed_roles"]` through the existing ingest pipeline. A chunk with no `allowed_roles` is world-readable.

### At retrieval — `RbacRetrievalBehavior : IRetrievalBehavior`

Registered in the pipeline after `RetrievalGuardBehavior`. Resolves `ICallerContext` from DI. A chunk passes when it has no `allowed_roles` metadata, or when its roles intersect the caller's roles (case-insensitive). Records `rbac_filtered_count` in `ctx.Extensions` for observability. Pass-through when `ICallerContext` is not registered.

### ASP.NET Core binding — `Rag.NET.Security.AspNetCore` (new package)

```csharp
public sealed class ClaimsPrincipalCallerContext(IHttpContextAccessor accessor) : ICallerContext
{
    public IReadOnlyList<string> GetRoles() =>
        accessor.HttpContext?.User.FindAll(ClaimTypes.Role)
                .Select(c => c.Value).ToList() ?? [];
}
```

### Registration

```csharp
// Framework-agnostic pipeline registration:
services.AddRagNet(b => b.UseRbac());

// In an ASP.NET Core project, add the ClaimsPrincipal binding:
services.AddRagNetAspNetCoreSecurity();
```

---

## Feature 2 — PII Detection & Redaction

Builds on the existing `IChunkSanitiser` abstraction and `ChunkSanitiserBehavior` in the ingest pipeline. Two new implementations run sequentially when both are registered — the existing behavior iterates all registered `IChunkSanitiser` instances in order.

### `PiiChunkSanitiser` (regex layer)

Typed placeholder replacement. Patterns compiled at construction time via `new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase)`.

| Built-in pattern | Placeholder |
|---|---|
| Email addresses | `[EMAIL]` |
| Phone numbers (E.164 + common formats) | `[PHONE]` |
| SSN (`\d{3}-\d{2}-\d{4}`) | `[SSN]` |
| Credit card numbers (Luhn-shaped) | `[CREDIT_CARD]` |
| IPv4/IPv6 addresses | `[IP_ADDRESS]` |

Each match is logged via `[LoggerMessage]` at `Warning` level with the document file name and placeholder type — never the matched value.

### `LlmPiiChunkSanitiser` (LLM layer)

Calls `IChatClient` with a structured prompt to identify and replace all PII with typed placeholders. Falls back to the original text on failure — same non-throwing contract as all sanitisers. Registers after the regex layer when both are enabled.

### `PiiDetectionOptions` — configurable and extensible

```csharp
public sealed record PiiPattern
{
    public required string Placeholder { get; init; }  // e.g. "[EMPLOYEE_ID]"
    public required string RegexPattern { get; init; }
}

public sealed class PiiDetectionOptions
{
    // Pre-populated with the five built-in patterns
    public IList<PiiPattern> Patterns { get; init; } = PiiPatterns.Defaults.ToList();
}
```

Built-in patterns are exposed as static readonly fields on `PiiPatterns` so callers can reference or remove them individually.

### Registration

```csharp
services.AddRagNet(b => b
    .UsePiiDetection(o =>
    {
        // Add a domain-specific pattern
        o.Patterns.Add(new PiiPattern
        {
            Placeholder = "[EMPLOYEE_ID]",
            RegexPattern = @"\bEMP-\d{6}\b"
        });

        // Remove a built-in
        o.Patterns.Remove(PiiPatterns.IpAddress);
    })
    .UseLlmPiiDetection());   // optional LLM pass on top of regex
```

> **Note:** Options are code-only (no `IOptions<T>`) — consistent with the rest of the pipeline layer. `IOptions` alignment + ZeroAlloc Validation is tracked as a separate backlog item.

---

## Feature 3 — Audit Log

### Abstraction

```csharp
public interface IAuditLog
{
    ValueTask LogRetrievalAsync(AuditRetrievalEvent ev, CancellationToken ct = default);
    ValueTask LogAnswerAsync(AuditAnswerEvent ev, CancellationToken ct = default);
}
```

### Event records

```csharp
public sealed record AuditRetrievalEvent
{
    public required string RequestId         { get; init; }  // correlates retrieval ↔ answer
    public required DateTimeOffset Timestamp  { get; init; }
    public required IReadOnlyList<string> CallerRoles   { get; init; }
    public required IReadOnlyList<AuditChunkRef> Chunks { get; init; }  // doc ID + chunk index + score
    public required IReadOnlyList<AuditGuardAction> GuardActions { get; init; }
    public string? Query  { get; init; }   // null unless AuditLogOptions.LogQueryText = true
}

public sealed record AuditAnswerEvent
{
    public required string RequestId         { get; init; }
    public required DateTimeOffset Timestamp  { get; init; }
    public string? Answer { get; init; }   // null unless AuditLogOptions.LogAnswerText = true
}

public sealed record AuditChunkRef
{
    public required string DocumentId  { get; init; }
    public required int    ChunkIndex  { get; init; }
    public required double Score       { get; init; }
}

public sealed record AuditGuardAction
{
    public required string Stage       { get; init; }  // e.g. "rbac", "trust_level", "regex_guard"
    public required string Action      { get; init; }  // e.g. "filtered", "redacted"
    public required string DocumentId  { get; init; }
}
```

### Integration points

- **`AuditRetrievalBehavior : IRetrievalBehavior`** — runs after retrieval. Generates a `RequestId` (GUID), stores it in `ctx.Extensions["audit_request_id"]` for answer correlation. Collects guard actions from `ctx.Extensions` (each guard stamps its actions there). Writes `AuditRetrievalEvent` asynchronously.
- **`AuditAnswerEngineDecorator : IAnswerEngine`** — wraps any `IAnswerEngine`. Reads `RequestId` from retrieval context, writes `AuditAnswerEvent` after generation. Errors are logged, never thrown.

### Implementations

- **`SqliteAuditLog`** — persists to a SQLite file. Tables: `retrieval_events` and `answer_events`. Async fire-and-forget writes (errors logged, never re-thrown to caller).
- **`NoOpAuditLog`** — registered automatically when `UseAuditLog()` is not called, so behaviors compile without requiring an implementation.

### Options

```csharp
public sealed class AuditLogOptions
{
    public bool   LogQueryText  { get; set; } = false;   // opt-in to store raw query
    public bool   LogAnswerText { get; set; } = false;   // opt-in to store generated answer
    public string DatabasePath  { get; set; } = "rag-audit.db";
}
```

### Registration

```csharp
services.AddRagNet(b => b
    .UseAuditLog(o =>
    {
        o.LogQueryText  = true;
        o.LogAnswerText = true;
        o.DatabasePath  = "/var/data/rag-audit.db";
    }));
```

---

## Package structure

| Package | Changes |
|---|---|
| `Rag.NET.Security` | Add `ICallerContext`, `RbacRetrievalBehavior`, `PiiChunkSanitiser`, `LlmPiiChunkSanitiser`, `PiiDetectionOptions`, `PiiPatterns`, `IAuditLog`, `AuditRetrievalBehavior`, `AuditAnswerEngineDecorator`, `SqliteAuditLog`, `NoOpAuditLog`, event records, builder extensions |
| `Rag.NET.Security.AspNetCore` | New thin package: `ClaimsPrincipalCallerContext`, `AddRagNetAspNetCoreSecurity()` |

---

## What is not in scope

- `IOptions<T>` alignment for pipeline options — tracked separately in features backlog (with ZeroAlloc Validation)
- Reversible PII tokenisation (re-identify for authorised retrieval) — future follow-up
- `ICallerContext` implementations for non-ASP.NET Core frameworks (gRPC, Worker Services) — callers provide their own
