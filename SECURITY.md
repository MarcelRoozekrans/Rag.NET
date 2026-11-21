# Security Policy

## Supported versions

Rag.NET follows the [security policy of the product line](https://github.com/MarcelRoozekrans/product-line/blob/main/SECURITY.md).

* The latest release receives security fixes. Today that is the 1.x line; `0.1.0` is not supported.
* After a new major version, the previous major still receives fixes for critical and high findings
  for 6 months. After that, only the latest release.

## Reporting a vulnerability

**Please report privately, not as a public issue.**

Use GitHub's private vulnerability reporting: go to the
[Security tab](https://github.com/MarcelRoozekrans/Rag.NET/security) and choose **Report a
vulnerability**. That gives you a private thread with the maintainer and a tracked advisory.

A public issue is the one thing to avoid — it discloses the problem to everyone before there is
anything to upgrade to.

### What to expect

* A confirmation of receipt within 5 working days.
* A first assessment, including severity, within 15 working days.
* The aim is to fix the issue and disclose it together with you within 90 days, sooner for critical
  findings.
* If a report is valid and you want credit in the advisory, say so and you will get it.

No bug bounty.

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
