# Design: MCP fail-closed — give library consumers the guard the CLI already has

**Issue:** #198 · **Phase:** 6.2.13 · **Date:** 2026-09-06

## 0. What is actually broken

`Rag.NET.Mcp` exposes ingest as well as retrieve and ask. Over HTTP that is a **remote write
surface**: a stranger who can reach the port can write into the corpus that answers your users'
questions, and retrieval degrades silently rather than erroring, so there is no moment where anyone
notices.

**The CLI is already guarded.** `Rag.NET.Mcp.Tool/Program.cs:51` refuses to start the HTTP transport
without a key unless `--allow-anonymous`, and line 84 checks every request through
`McpApiKeyAuthorization.IsAuthorized`. **The library path is not.** A consumer following
`docs/guide/mcp.mdx:68`:

```csharp
builder.Services.AddRagNetMcpServer().Server.WithHttpTransport();
```

gets an unauthenticated write surface, and the documentation's own remedy (line 76) is hand-rolled
middleware comparing a header to a literal.

`McpApiKeyAuthorization` is unit-tested as a decision function. **Nothing exercises a host**, so the
red run the issue asks for — stand the host up with no auth, call `rag_ingest`, assert rejection —
does not exist.

## 1. Why #189's fix cannot be ported

`Rag.NET.Api` fails closed by a marker: `UseRagNetApiAuthentication` sets
`ApiKeyMiddlewareMarker.IsRegistered`, and `MapRagNetApi` throws when it is unset and
`AllowAnonymous` is not chosen. Two reasons that shape is unavailable here:

1. **There is no `MapRagNetMcp` to guard.** HTTP is reached through the SDK's own
   `WithHttpTransport` extension on the SDK's own `IMcpServerBuilder`. No marker in `Rag.NET.Mcp`
   can observe that call — the package is not on either end of it.
2. **`Rag.NET.Mcp` refuses the ASP.NET Core dependency**, deliberately: stdio consumers host MCP
   tools in non-web processes, and `McpServerBuilder`'s own remarks record that a
   `WithHttpTransport` wrapper *existed before and silently did nothing, because it had nothing real
   to call*.

## 2. The decision

**A new package, `Rag.NET.Mcp.AspNetCore`.** It may reference `ModelContextProtocol.AspNetCore` and
ASP.NET Core because only web hosts consume it, and it can therefore provide the wrapper
`Rag.NET.Mcp` cannot honestly provide.

**Precedent, applied a second time.** Phase 6.2.6 split `SqliteAuditLog` into
`Rag.NET.Security.Audit.Sqlite` so `Rag.NET.Security` would stop dragging a native SQLite binary —
and *removed* `UseAuditLog()` rather than keeping it as a runtime check, so that "auditing
configured, nothing recorded" could not be expressed at all. The same standard applies here: the
goal is not to warn about the unauthenticated host but to make it **unreachable by accident**.

## 3. Shape

```csharp
builder.Services
    .AddRagNetMcpServer()
    .WithRagNetHttpTransport(o => o.ApiKey = "…");   // or o.AllowAnonymous = true

app.MapRagNetMcp("/mcp");
```

**`WithRagNetHttpTransport` throws when neither a key nor an explicit opt-out is configured.** That
is the earliest point at which the decision is knowable, and it fails at startup rather than on the
first unauthenticated request.

**`MapRagNetMcp` attaches the key check as an endpoint filter on the endpoints it maps**, rather
than as middleware the consumer installs separately. This is deliberately *stronger* than #189's
marker:

| | `Rag.NET.Api` (#189) | here |
|---|---|---|
| auth is installed by | a separate `Use` call the consumer must remember | the `Map` call itself |
| wrong order | detected, and throws | not expressible |
| forgetting entirely | detected, and throws | not expressible |

The marker exists in `Rag.NET.Api` because middleware and endpoints are assembled through two
builders that cannot see each other. `MapMcp` returns an `IEndpointConventionBuilder`, so here the
two ends are the same object and the guard can be structural instead of detected.

**Scope of the filter is the MCP endpoints only.** A global `Use` — which is what the CLI does —
would authenticate the whole host, which is wrong for an application that mounts MCP beside its own
unrelated endpoints.

## 4. What is deliberately not in scope

- **Any scheme beyond the shared key.** OAuth, per-client identity, revocation and rotation are
  absent, exactly as they are on the other three surfaces. The pointer will say so rather than
  letting "authenticated" imply more than a shared secret.
- **Changing `Rag.NET.Mcp`.** It keeps `Server`, keeps stdio, and gains nothing — a stdio consumer
  has no network exposure and must not be made to opt out of a risk they do not have.
- **Rewriting the CLI.** It is already correct. It may later be reduced to a consumer of this
  package; that is not this phase's business.

## 5. How it will be proved

The exit condition's red run, and it must be mutation-checked:

1. Stand up a real `WebApplication` with `AddRagNetMcpServer().WithRagNetHttpTransport(o => o.ApiKey = "k")`
   and `MapRagNetMcp("/mcp")`.
2. Call the endpoint **without** the header → **401**.
3. Call it **with** the wrong key → **401**.
4. Call it **with** the right key → not 401.
5. Configuring neither key nor opt-out → `WithRagNetHttpTransport` **throws**.
6. `AllowAnonymous` → no throw, and the endpoint serves.

**The mutation that matters**: deleting the filter registration must fail (2) and (3). A test that
only asserts the happy path would pass against a host with no auth at all — which is the defect
being fixed.

## 6. Consequences

- Package count 72 → 73.
- `docs/guide/mcp.mdx` stops teaching hand-rolled middleware and teaches this instead.
- The new package carries an `Exercised by:` pointer and must not join `PackagesAllowedToStayUnit`.
