# robots.txt follows RFC 9309 section 2.2 (#927)

**Date:** 2026-11-14 · **Issue:** #927 · **Ships as:** `fix:`

## Intent

`RobotsTxt` (`src/Rag.NET.DataProviders.Web/RobotsTxt.cs`), shared by `WebCrawlerDataProvider` and
`SitemapDataProvider`, reads only prefix `Disallow` lines under `User-agent: *`, matched
case-insensitively. Real files use `Allow`, `*` and `$` (Drupal's stock file is full of
`Allow: /core/*.css$`), name crawlers in groups, and list several agents per group. Each gap makes
the providers skip pages a site allows or fetch pages it disallows. The parser is brought up to
RFC 9309 section 2.2, and `Crawl-delay` gets the meaning the issue asks for.

## Goals

- **Groups (2.1, 2.2.1).** Consecutive `user-agent` lines open one group; the group's rules follow
  them. Every group naming our product token applies, merged into one. When none does, every `*`
  group applies, merged. When neither exists, nothing is disallowed. Rules before the first
  `user-agent` line are ignored. Record names are case-insensitive; other records, `Sitemap`
  included, neither end a group nor break one.
- **Our product token.** Read from the `HttpClient` the provider was given: the first
  `ProductInfoHeaderValue` in `DefaultRequestHeaders.UserAgent` that carries a product, its name
  only (`MyBot` from `MyBot/1.0`). A name with characters outside `a-z A-Z _ -` is not a valid
  token (2.2.1), and neither is a missing header; both leave only `*` groups to apply. Matching is
  case-insensitive. A `user-agent` value in the file is cut at its first character outside that
  set, so `user-agent: MyBot/2.0` still names `MyBot`. (Operator's choice, 2026-11-14: no new
  option, which keeps this a fix.)
- **Matching (2.2.2, 2.2.3).** Every `allow` and `disallow` rule of the applicable group is matched
  against the URL's path and query from its first octet, case-sensitively. `*` matches any run of
  octets, `$` at the end of a rule anchors it to the end of the URL. The matching rule with the
  most octets wins; on a tie `allow` wins. No match allows. An empty `disallow` value is no rule.
  `/robots.txt` is always allowed. `#` starts a comment anywhere on a line.
- **Percent-encoding (2.2.2).** Before comparison, the URL and each rule are brought to one form:
  octets outside ASCII percent-encoded as UTF-8, `%XX` of an unreserved character (RFC 3986:
  `A-Z a-z 0-9 - . _ ~`) decoded, and other `%XX` kept with upper-case hex. So
  `/foo/bar/%62%61%7A` matches `disallow: /foo/bar/baz`, and `%2F` never matches `/`.
- **Crawl-delay.** The applicable group's `crawl-delay` (seconds, decimals allowed) raises the
  provider's request interval to that value when it is larger, and never lowers it below
  `MinimumRequestInterval`. A value that does not parse as a non-negative number is ignored. Two
  `crawl-delay` lines in the applicable groups: the larger counts.
- Tests in `Rag.NET.DataProviders.Web.Tests` against `RobotsTxt.Parse`, covering each rule above,
  plus one test per provider showing a `Crawl-delay` spacing its requests.

## Non-goals

- **Access-method behaviour (2.3)**: redirects, status codes, caching, the 500 KiB parse limit. In
  particular, RFC 9309 2.3.1.4 says an unreachable robots.txt or a 5xx response means "disallow
  everything"; today it allows everything. That changes what a crawl does when a site has an
  outage, so it is #998, for the operator's call, not changed here.
- A per-host request interval. `SitemapDataProvider` has one throttle for every host it visits;
  a `Crawl-delay` from any host raises it for all of them. Slower than needed on multi-host
  sitemaps, never faster than a site asked.
- A user-agent option on `WebCrawlerOptions` or `SitemapOptions` (new public API).

## Anti-goals

- Fetching a page the applicable group disallows, or skipping one it allows, for any of the
  examples in RFC 9309 sections 2.2.1 to 2.2.3.
- A `Crawl-delay` making requests faster than `MinimumRequestInterval`.
- A matcher whose running time explodes on rules with many `*` (no backtracking regex).

## Approaches

1. **Hand-written matcher (chosen).** Each rule is stored as its normalised pattern; matching is a
   linear-space wildcard walk over the normalised path-and-query, the way Google's reference parser
   does it. Small, no dependencies, predictable on hostile input.
2. **Translate each rule to a `Regex`.** Less code, but a site controls the pattern, so it needs
   `RegexOptions.NonBacktracking` or a timeout, and "most octets" has to be computed separately
   anyway. No gain.
3. **A NuGet robots.txt parser.** Adds a dependency to a package that has none for this, and none
   reads the agent from an `HttpClient`. Rejected.

## Shape

- `RobotsTxt.Parse(string content, string? productToken)` returns the applicable group's rules and
  its `CrawlDelay` (`TimeSpan?`). `IsDisallowed(string url)` keeps its signature.
- `RobotsTxt.LoadAsync` reads the product token from the `HttpClient` it already receives.
- `RequestThrottle` gains `RaiseTo(TimeSpan)`: the interval becomes the larger of the two. Both
  providers call it with `CrawlDelay` after a load. The throttle currently skips all work when its
  interval is zero; that check reads the current interval instead.
- XML docs on `RespectRobotsTxt` (both options classes) and `MinimumRequestInterval`, the class
  remarks on `RobotsTxt`, and the robots.txt paragraph in `docs/guide/data-providers.md` describe the
  new behaviour.

## Left to the builder

- The exact wildcard-walk algorithm and how normalisation is shared between URL and rule.
- Test names and grouping.
