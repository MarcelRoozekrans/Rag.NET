# An unreachable robots.txt disallows its host (#998)

**Date:** 2026-11-14 · **Issue:** #998 · **Ships as:** `fix:` · **Status:** approved 2026-11-14

## Intent

`RobotsTxt.LoadAsync` (`src/Rag.NET.DataProviders.Web/RobotsTxt.cs`) allows everything whenever
the robots.txt fetch fails. RFC 9309 section 2.3.1.4 says a robots.txt that is unreachable,
because of a 5xx status or a network error, MUST be read as complete disallow. So today a site
whose robots.txt returns 503 during an outage gets crawled in full by `WebCrawlerDataProvider`
and `SitemapDataProvider`, including the paths it disallows.

The operator chose to follow the RFC and to report the host that was skipped (2026-11-14).

## Goals

- **Classify the fetch result (2.3.1).**
  - Success: parse the file, as today.
  - 4xx (unavailable, 2.3.1.3): allow everything, as today. This includes a 3xx left over after
    `HttpClient` gives up on redirects, which 2.3.1.2 lets a crawler treat as unavailable.
  - 5xx, **429**, or no response at all (network error, a body that breaks off, an `HttpClient`
    timeout): unreachable, so disallow every URL on that host. Treating 429 as unreachable is the
    operator's choice: it means "slow down", not "there is no file". Google does the same.
  - Caller cancellation still propagates.
- **Report each skipped host once.** The provider yields one `Result.Failure` per unreachable
  host and lists no pages from it:
  - `RagError.HttpFailed(status, message)` for a 5xx or 429. The message names the robots.txt
    URL and says that no page from that host was crawled. `HttpFailed.Content` is documented as
    "the response body or an explanatory message", so this needs no new API.
  - `RagError.TransportFailed(inner)` when no response came back. `inner` is an
    `HttpRequestException` whose message names the robots.txt URL, wrapping the original
    exception.
  - The web crawler reports its seed host. The sitemap provider reports each host the first time
    one of that host's URLs needs a robots check.
- **Parse at most 500 KiB (2.5).** Only the first 512,000 bytes of the body are read. If the file
  is longer, the partial last line is dropped, so a rule that was cut off is never applied in its
  shortened form.

## Why reporting is required

A listing failure is the existing signal that a run did not see everything.
`IngestFromProviderAsync` adds it to `ProviderIngestionResult.Errors` and does not run
`CleanupMode.Full` (#400). If the host were skipped silently, the provider would list nothing for
it, and full cleanup would delete every document already ingested from it. A robots.txt outage
must not wipe a site out of the index.

## Non-goals

- **A redirect limit.** The issue reads 2.3.1.2 as a limit of five. The RFC says crawlers SHOULD
  follow *at least* five. `HttpClient` follows up to 50 by default, and the caller owns that
  handler, so nothing changes here. The issue gets a correction when the PR closes it.
- **The 30-day fallback and caching (2.3.1.4, 2.4).** These are MAY rules, and the providers keep
  no state between runs.
- **An option to keep today's allow-all behaviour.** `RespectRobotsTxt = false` already turns
  robots handling off entirely.

## Approaches

1. **Use the existing `RagError` types (chosen).** `HttpFailed` or `TransportFailed`, with a message
   that names the robots.txt URL. No public API changes, and it fits how the providers already
   report failures.
2. **Add a `RagError.RobotsTxtUnreachable` variant.** Callers could match on it, but it adds a
   permanent public type to Abstractions for one case. Rejected by the operator.
3. **Skip without reporting.** Rejected: see "Why reporting is required".

## Left to the builder

- How `LoadAsync` returns the unreachable state alongside a `RobotsTxt` (for example a result
  type, or a `RobotsTxt.Unreachable` instance that carries the error).
- Where the byte cap lives: a `maxBytes` parameter on `GetStringWithCharsetFallbackAsync`, or a
  robots-only reader. The charset fallback and the BOM handling stay as they are.
- Tests: 5xx, 429, 404, a network error, an `HttpClient` timeout and caller cancellation, for both
  providers. Also: the one-failure-per-host report, full cleanup blocked end to end, and a
  >500 KiB file whose cut rule is not applied. Update the README and the `RobotsTxt` doc comments.
