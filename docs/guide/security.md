---
id: security
title: Security
sidebar_position: 9
---

# Security

The security layer adds three independent, composable features to a Rag.NET pipeline:

- **RBAC on chunks** — filters retrieved chunks to those the current caller is allowed to see.
- **PII detection and redaction** — scrubs personal data from chunk text at ingest time, before embeddings are stored.
- **Audit log** — records every retrieval and answer event to a SQLite database for compliance and forensics.

All three are opt-in. Register any combination of them through the `RagBuilder` API. They have no mandatory coupling — you can use the audit log without RBAC, or PII redaction without the audit log.

## RBAC on Chunks

Role-based access control filters retrieved chunks based on an `allowed_roles` metadata key. Chunks that do not carry the key are world-readable and pass through for every caller.

### Tagging documents at ingest

Attach an `allowed_roles` entry in `DocumentMetadata.Tags` when ingesting a document. The value is a comma-separated, case-insensitive list of role names:

```csharp
await pipeline.IngestAsync(stream, new DocumentMetadata
{
    DocumentId = new DocumentId("hr-handbook-2024"),
    FileName   = "hr-handbook-2024.pdf",
    Tags       = new Dictionary<string, MetadataValue>
    {
        ["allowed_roles"] = "hr,finance",
    },
});
```

`MetadataBehavior` propagates all `Tags` entries into each chunk's metadata dictionary at ingest time. After ingestion every chunk produced from the document carries `allowed_roles = "hr,finance"` in its stored metadata.

Chunks from documents that have no `allowed_roles` tag are always returned, regardless of who is calling.

### `ICallerContext`

`RbacRetrievalGuard` resolves the caller's roles at retrieval time by calling `ICallerContext.GetRoles()`. The interface is framework-agnostic:

```csharp
public interface ICallerContext
{
    IReadOnlyList<string> GetRoles();
}
```

Implement it as a singleton. For non-web hosts (console, worker service, batch job) use `AsyncLocal<IReadOnlyList<string>>` to flow roles per logical call:

```csharp
public sealed class AsyncLocalCallerContext : ICallerContext
{
    private static readonly AsyncLocal<IReadOnlyList<string>> _roles = new();

    public static void SetRoles(IReadOnlyList<string> roles) =>
        _roles.Value = roles;

    public IReadOnlyList<string> GetRoles() =>
        _roles.Value ?? [];
}
```

Register it before calling `UseRbac()`:

```csharp
services.AddSingleton<ICallerContext, AsyncLocalCallerContext>();
services.AddRagNet(b => b.UseRbac());
```

### ASP.NET Core integration

The `Rag.NET.Security.AspNetCore` package provides `ClaimsPrincipalCallerContext`, which reads roles from the current `ClaimsPrincipal` via `IHttpContextAccessor`. Call `AddRagNetAspNetCoreSecurity()` after `AddRagNet`:

```csharp
services.AddRagNet(b => b.UseRbac());
services.AddRagNetAspNetCoreSecurity();
```

`AddRagNetAspNetCoreSecurity` also calls `AddHttpContextAccessor()`, so no separate registration is needed.

### Registration

```csharp
services.AddRagNet(b => b
    .UseRbac());
```

> **Note:** `UseRbac()` resolves `ICallerContext` as a required service. If no implementation is registered, the application will throw `InvalidOperationException` at first retrieval. Always register `ICallerContext` before the pipeline is first used — either via `AddRagNetAspNetCoreSecurity()` or a custom implementation.

## PII Detection and Redaction

PII detection protects stored embeddings by scrubbing personal data from chunk text at ingest time, before any data reaches the vector store. It is implemented by `IChunkSanitiser` and runs as part of the ingestion pipeline.

Two modes are available: regex-based and LLM-based. They can be registered independently or chained — multiple `IChunkSanitiser` registrations are applied in order.

### Regex detection (`UsePiiDetection`)

Uses compiled regular expressions to find and replace PII patterns. All built-in patterns are active by default:

```csharp
services.AddRagNet(b => b
    .UsePiiDetection());
```

#### Built-in patterns

| Pattern | Placeholder | Matches |
|---------|-------------|---------|
| `PiiPatterns.Email` | `[EMAIL]` | RFC 5321 email addresses |
| `PiiPatterns.Phone` | `[PHONE]` | US phone numbers (various formats, optional country code) |
| `PiiPatterns.Ssn` | `[SSN]` | US Social Security numbers (`\d{3}-\d{2}-\d{4}`) |
| `PiiPatterns.CreditCard` | `[CREDIT_CARD]` | Visa, Mastercard, Discover, Amex card numbers |
| `PiiPatterns.IpAddress` | `[IP_ADDRESS]` | IPv4 and IPv6 addresses |

A chunk containing `"Contact john@example.com or call 555-867-5309"` becomes `"Contact [EMAIL] or call [PHONE]"`.

#### Configuring patterns

Add custom patterns or remove built-ins by configuring `PiiDetectionOptions`:

```csharp
services.AddRagNet(b => b
    .UsePiiDetection(o =>
    {
        // Remove SSN detection
        o.Patterns.Remove(PiiPatterns.Ssn);

        // Add a custom pattern — Dutch BSN number
        o.Patterns.Add(new PiiPattern
        {
            Placeholder  = "[BSN]",
            RegexPattern = @"\b\d{9}\b",
        });
    }));
```

`PiiDetectionOptions.Patterns` is pre-populated with `PiiPatterns.Defaults`. Any modification before `IChunkSanitiser` is constructed takes effect immediately — patterns are compiled once at construction time.

#### Timeout protection

Each regex is evaluated with a 1-second timeout. If a regex times out (pathological backtracking on a long chunk), the sanitiser logs a warning and returns the original text unchanged. Downstream processing is never blocked.

### LLM detection (`UseLlmPiiDetection`)

Asks a registered `IChatClient` to identify and redact PII. Covers patterns that are difficult to express as regular expressions (names, addresses, contextual identifiers):

```csharp
services.AddRagNet(b => b
    .UseLlmPiiDetection());
```

An `IChatClient` must be registered in DI before calling `UseLlmPiiDetection()`.

### Chaining regex and LLM detection

Register both to apply regex redaction first, then pass the partially-redacted text to the LLM:

```csharp
services.AddRagNet(b => b
    .UsePiiDetection()      // regex pass — fast, deterministic
    .UseLlmPiiDetection()); // LLM pass — catches names, addresses, context-dependent PII
```

Sanitisers are applied in registration order. The LLM sees already-redacted text, which reduces both cost and hallucination risk.

## Audit Log

The audit log captures every retrieval and answer event to a SQLite database. It is designed for compliance scenarios: who retrieved which chunks, when, and (optionally) what they asked.

### What is captured

**`AuditRetrievalEvent`** — written after every `RetrieveAsync` call:

| Field | Type | Notes |
|-------|------|-------|
| `RequestId` | `string` | Shared with the corresponding `AuditAnswerEvent` |
| `Timestamp` | `DateTimeOffset` | UTC timestamp of the retrieval |
| `CallerRoles` | `IReadOnlyList<string>` | Roles from `ICallerContext`, or empty list when RBAC is not active |
| `Chunks` | `IReadOnlyList<AuditChunkRef>` | `DocumentId`, `ChunkIndex`, and `Score` for each returned chunk |
| `Query` | `string?` | Raw query text — only populated when `LogQueryText = true` |

**`AuditAnswerEvent`** — written after every `AskAsync` call:

| Field | Type | Notes |
|-------|------|-------|
| `RequestId` | `string` | Matches the `RequestId` of the preceding `AuditRetrievalEvent` |
| `Timestamp` | `DateTimeOffset` | UTC timestamp of answer generation |
| `Answer` | `string?` | Generated answer text — only populated when `LogAnswerText = true` |

### Privacy defaults

By default neither the query text nor the answer text is stored:

```csharp
public sealed class AuditLogOptions
{
    public bool   LogQueryText  { get; set; } = false;
    public bool   LogAnswerText { get; set; } = false;
    public string DatabasePath  { get; set; } = "rag-audit.db";
}
```

Enable them explicitly when your compliance policy requires it:

```csharp
services.AddRagNet(b => b
    .UseAuditLog(o =>
    {
        o.LogQueryText  = true;
        o.LogAnswerText = true;
        o.DatabasePath  = "/var/data/audit.db";
    }));
```

### Correlation

`AuditRetrievalBehavior` generates a `RequestId` (a `Guid`) after retrieval and stores it in `AuditCorrelationContext`, which is an `AsyncLocal`-backed singleton. `AuditAnswerEngineDecorator` reads the same `RequestId` from `AuditCorrelationContext` when writing the answer event. No extra setup is required — the correlation is automatic for any call that goes through `AskAsync` (which calls retrieval then generation in sequence).

If you call `RetrieveAsync` and `AskAsync` independently (e.g., streaming scenarios), the `RequestId` flows correctly as long as both calls share the same async execution context.

### Registration

```csharp
services.AddRagNet(b => b
    .UseAuditLog(o =>
    {
        o.LogQueryText = true;
        o.DatabasePath = "/var/data/audit.db";
    }));
```

The SQLite database and tables are created lazily on the first write. Write failures are logged as warnings and never thrown — the pipeline continues normally even if the audit log is unavailable.

### SQLite tables

The audit database contains two tables:

```sql
CREATE TABLE retrieval_events (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id   TEXT NOT NULL,
    timestamp    TEXT NOT NULL,   -- ISO 8601 UTC
    caller_roles TEXT NOT NULL,   -- JSON array of strings
    chunks       TEXT NOT NULL,   -- JSON array of {documentId, chunkIndex, score}
    query        TEXT             -- NULL unless LogQueryText = true
);

CREATE TABLE answer_events (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id TEXT NOT NULL,
    timestamp  TEXT NOT NULL,   -- ISO 8601 UTC
    answer     TEXT             -- NULL unless LogAnswerText = true
);
```

Query all retrieval events for a user role in the last 24 hours:

```sql
SELECT request_id, timestamp, caller_roles, chunks
FROM   retrieval_events
WHERE  json_each.value = 'hr'
  AND  timestamp >= datetime('now', '-1 day')
FROM   retrieval_events, json_each(caller_roles);
```

Join retrieval and answer events by `request_id`:

```sql
SELECT r.timestamp, r.caller_roles, r.query, a.answer
FROM   retrieval_events r
JOIN   answer_events    a ON a.request_id = r.request_id
ORDER  BY r.timestamp DESC
LIMIT  100;
```

## Composing Features

All three features are independent and compose freely. Register them in a single `AddRagNet` block:

```csharp
// ASP.NET Core — RBAC + PII redaction + audit log
services.AddRagNet(b => b
    .UseRbac()
    .UsePiiDetection(o =>
    {
        o.Patterns.Remove(PiiPatterns.Ssn); // not applicable for this corpus
    })
    .UseLlmPiiDetection()
    .UseAuditLog(o =>
    {
        o.LogQueryText = true;
        o.DatabasePath = "/var/data/audit.db";
    }));

services.AddRagNetAspNetCoreSecurity(); // wires ClaimsPrincipalCallerContext for UseRbac
```

The registration order within the builder determines sanitiser chain order (regex before LLM). RBAC filtering and audit logging are independent of registration order relative to PII.

> **Note:** `UseAuditLog` must be called after `AddRagNet` so that `RetrievalPipelineBuilder` is already registered in DI. Calling it before `AddRagNet` throws `InvalidOperationException`.

Answer auditing is independent of registration order relative to the answer engines. `UseAuditLog` adds its decorator to the answer-engine decorations `RagPipeline` applies when it composes its engine, so `rag.UseAuditLog().UseMapReduceAnswerEngine()` and the reverse both audit every answer. Both used to register `IAnswerEngine` directly, so last-wins dropped whichever ran first — while retrieval auditing kept working, leaving an audit log that read as complete and recorded no answers at all ([#195](https://github.com/MarcelRoozekrans/Rag.NET/issues/195)). Resolving `IAnswerEngine` yields the *registered* engine, undecorated; `ComposedAnswerEngine` is the audited one.
