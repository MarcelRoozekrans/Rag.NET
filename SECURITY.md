# Security Policy

## Supported versions

| Version | Supported |
|---|---|
| 0.1.0 | ✅ the only published release |
| < 0.1.0 | ❌ never published |

Rag.NET is **pre-1.0**. There is one published version, `0.1.0`, across 71 packages on nuget.org.
Fixes ship in the next release rather than as backports — there is no branch to backport to, and
saying so is more useful than implying a support matrix that does not exist yet.

## Reporting a vulnerability

**Please report privately, not as a public issue.**

Use GitHub's private vulnerability reporting: go to the
[Security tab](https://github.com/MarcelRoozekrans/Rag.NET/security) and choose **Report a
vulnerability**. That gives you a private thread with the maintainer and a tracked advisory.

A public issue is the one thing to avoid — it discloses the problem to everyone before there is
anything to upgrade to.

### What to expect

This is a small project, so the honest answer is **best effort**: an acknowledgement within about a
week, and a fix in a normal release once there is one. If a report is valid and you want credit in
the advisory, say so and you will get it.

No bug bounty. No embargo demands in either direction.

## What is in scope

Anything in the published NuGet packages — the retrieval pipeline, the security features
(RBAC, PII redaction, audit logging, prompt-injection defences), and the hosting surfaces
(`Rag.NET.Api`, `Rag.NET.Api.Grpc`, `Rag.NET.Mcp*`).

Before reporting, it is worth reading
[the security posture](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/security.md#security-posture),
which states what the library defends against and what it deliberately leaves to the host
application. A report that the library does not authenticate your end users is answered there rather
than by an advisory.

## What is out of scope

- **The documentation site build and the benchmark harnesses.** `package.json` builds the Docusaurus
  site and `benchmarks/library-comparison-python/` runs a comparison harness. Neither is published;
  neither is in any NuGet package's dependency closure. Advisories against their dependencies are
  tracked but are not vulnerabilities in Rag.NET — the posture document explains the current set.
- **A consumer's own configuration.** Choosing `AllowAnonymous`, omitting RBAC tags, or pointing the
  library at an unsecured vector store are configuration decisions the library documents rather than
  defects in it.
- **Vulnerabilities in a backing store or model provider.** Report those to that project; if Rag.NET
  makes one materially easier to exploit, that part is in scope and worth reporting here too.
