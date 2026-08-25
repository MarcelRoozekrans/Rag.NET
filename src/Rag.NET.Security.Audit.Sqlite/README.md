# Rag.NET.Security.Audit.Sqlite

SQLite-backed audit logging for Rag.NET.Security: records retrieval and answer events to a local
database file, with the audit behaviour and the answer-engine decorator wired in one call.

## Install

```bash
dotnet add package Rag.NET.Security.Audit.Sqlite
```

## Setup

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Security;

services.AddRagNet(rag => rag
    .UseSqliteAuditLog(o =>
    {
        o.DatabasePath = "rag-audit.db";
        o.LogQueryText = true;   // off by default
        o.LogAnswerText = true;  // off by default
    }));
```

`LogQueryText` and `LogAnswerText` are off by default: the audit record captures *that* a retrieval
and an answer happened, with their correlation, without persisting the text unless you ask for it.

`UseSqliteAuditLog` must be called after `AddRagNet` — it adds a behaviour to the retrieval pipeline
that `AddRagNet` registers. Calling it first throws `InvalidOperationException` naming the problem.

Registration order relative to the answer engines does not matter. The decorator is added to the
answer-engine decoration seam rather than by registering `IAnswerEngine`, so
`rag.UseSqliteAuditLog().UseMapReduceAnswerEngine()` and the reverse both audit every answer.

## Why this is a separate package

`SqliteAuditLog` used to live in `Rag.NET.Security`, which meant `Microsoft.Data.Sqlite` and a
native `SQLitePCLRaw` binary shipped to everyone using `UseChunkSanitiser`, `UseRbac` or
`UsePiiDetection` — none of which load it
([#339](https://github.com/MarcelRoozekrans/Rag.NET/issues/339)).

## Migrating from 0.1.0

`UseAuditLog()` no longer exists. Two steps, and no `using` changes — the namespace is deliberately
still `Rag.NET.Security`:

1. Add a package reference to `Rag.NET.Security.Audit.Sqlite`.
2. Rename `UseAuditLog(…)` to `UseSqliteAuditLog(…)`.

Forgetting step 2 is a **compile error**, not a silent gap. The wiring that registers the audit
behaviour and the answer decorator is internal to `Rag.NET.Security` and reachable only from a
package that also supplies an `IAuditLog`, so "auditing configured, nothing recorded" cannot be
expressed. An audit log that silently records nothing is worse than a build error.

## Bringing your own log

`IAuditLog` lives in `Rag.NET.Security`. This package is one implementation of it; a different
backing store would be a sibling package registering its own `IAuditLog` and composing the same
internal wiring.
