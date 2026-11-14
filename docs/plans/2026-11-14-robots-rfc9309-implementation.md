# robots.txt RFC 9309 section 2.2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `RobotsTxt` applies the right group, matches `allow`/`disallow` with `*`/`$` by longest match, and its `Crawl-delay` raises the providers' request interval.

**Architecture:** `RobotsTxt.Parse` groups the file, picks the applicable groups for a product token, and keeps their rules as normalised patterns plus a crawl delay. A hand-written wildcard walk matches normalised path-and-query. `RequestThrottle` gains `RaiseTo`, called by both providers after each load.

**Tech Stack:** .NET 10, C#, xunit.v3.

**Spec:** `docs/plans/2026-11-14-robots-rfc9309-design.md`

## Global Constraints

- Ships as `fix:`; no new public API. `RobotsTxt` and `RequestThrottle` stay `internal`.
- Commit headers ≤ 100 characters, conventional type from `.commitlintrc.yml`; wrap bodies at ~95 columns; no `Word:` at the start of a body line; no nested parentheses; no Co-Authored-By trailer.
- `TreatWarningsAsErrors` is on; `HttpClient.GetStringAsync` is banned in this package.
- Matching is case-sensitive and on path plus query; record names and agent names are case-insensitive.
- A valid product token is `[A-Za-z_-]+` (RFC 9309 2.2.1).
- No backtracking regex for rule matching.

## Review Focus

- **A rule with many `*` against a long URL** (`/*a*a*a*a*a*b` vs 2,000 `a`s) must finish quickly: test in Task 2.
- **An agent named only with a version** (`User-Agent: MyBot/1.0 (+https://x)` on the client, `user-agent: mybot` in the file) must select the named group: test in Task 3.
- **`Sitemap:` between `user-agent` and its rules** must not end the group: test in Task 1.
- **Windows line endings and a UTF-8 BOM** must parse as normal lines: test in Task 1.
- **`Crawl-delay` on a `*` group when a named group applies** must be ignored: test in Task 3.

---

### Task 1: Groups and longest-match rules

**Files:**
- Modify: `src/Rag.NET.DataProviders.Web/RobotsTxt.cs`
- Modify: `src/Rag.NET.DataProviders.Web/Rag.NET.DataProviders.Web.csproj` (add `InternalsVisibleTo` `Rag.NET.DataProviders.Web.Tests`, the `AssemblyAttribute` form `Rag.NET.Reranking.Onnx.csproj` uses)
- Create: `tests/Rag.NET.DataProviders.Web.Tests/RobotsTxtTests.cs`

**Interfaces:**
- Produces: `internal static RobotsTxt Parse(string content, string? productToken)`; `public bool IsDisallowed(string url)` (unchanged signature); `internal TimeSpan? CrawlDelay { get; }` (always `null` until Task 3).
- The old `Parse(string content)` is removed; `LoadAsync` passes `null` for now.

- [ ] **Step 1: Write the failing tests** in `RobotsTxtTests.cs`. Helper: `static bool Blocked(string robots, string path, string? agent = null) => RobotsTxt.Parse(robots, agent).IsDisallowed("https://example.com" + path);`

  - `ConsecutiveUserAgentLines_FormOneGroup`: `"User-agent: a\nUser-agent: *\nDisallow: /x\n"` → `/x` blocked for `null`.
  - `NamedGroup_ReplacesTheStarGroup`: `"User-agent: *\nDisallow: /a\n\nUser-agent: MyBot\nDisallow: /b\n"` with agent `"mybot"` → `/a` allowed, `/b` blocked; with agent `null` → `/a` blocked, `/b` allowed.
  - `GroupsForTheSameAgent_AreMerged`: two `User-agent: *` groups disallowing `/foo` and `/baz` → both blocked.
  - `NoApplicableGroup_AllowsEverything`: only a `User-agent: OtherBot` group → `/anything` allowed.
  - `RulesBeforeTheFirstUserAgent_AreIgnored`: `"Disallow: /x\nUser-agent: *\nDisallow: /y\n"` → `/x` allowed, `/y` blocked.
  - `SitemapLine_DoesNotEndTheGroup`: `"User-agent: *\nSitemap: https://example.com/s.xml\nDisallow: /x\n"` → `/x` blocked.
  - `LongestMatchWins`: `"User-agent: *\nDisallow: /core/\nAllow: /core/public/\n"` → `/core/public/a` allowed, `/core/secret` blocked.
  - `AllowWinsATie`: `"User-agent: *\nDisallow: /p\nAllow: /p\n"` → `/p` allowed.
  - `MatchingIsCaseSensitive`: `Disallow: /Private` → `/private` allowed, `/Private/x` blocked.
  - `RecordNamesAreCaseInsensitive`: `"USER-AGENT: *\ndisallow: /x\n"` → `/x` blocked.
  - `EmptyDisallow_IsNoRule`: `"User-agent: *\nDisallow:\n"` → `/` allowed.
  - `RobotsTxtItself_IsAlwaysAllowed`: `Disallow: /` → `/robots.txt` allowed, `/a` blocked.
  - `CommentsAreStripped`: `"User-agent: * # all\nDisallow: /x # private\n"` → `/x` blocked, `/y` allowed.
  - `QueryIsPartOfThePath`: `Disallow: /search?q=` → `/search?q=cats` blocked, `/search` allowed.
  - `CrLfAndBom_Parse`: `"﻿User-agent: *\r\nDisallow: /x\r\n"` → `/x` blocked.

- [ ] **Step 2: Run them; expect compile failure** on `Parse(string, string?)`.
  Run: `dotnet test tests/Rag.NET.DataProviders.Web.Tests -c Release`

- [ ] **Step 3: Implement.** Parse lines (split on `\n`, trim, strip BOM, cut at `#`, split at first `:`, key trimmed and case-insensitive). A `user-agent` line after a rule line starts a new group; consecutive ones extend the current group's agent list. Agent values are cut at the first character outside `[A-Za-z_-]` (keep `*`). Rules are `(bool allow, string pattern)`; Task 2 adds wildcards, so here match a plain prefix with `string.CompareOrdinal`. Select: groups whose agents contain the token (OrdinalIgnoreCase); if none, groups containing `*`; merge their rules. `IsDisallowed`: target is `uri.PathAndQuery` (`"/"` if empty); `"/robots.txt"` → false; best = longest matching pattern, `allow` on a tie; disallowed when best is a disallow. Update the class `<summary>`/`<remarks>` to describe the RFC 9309 2.2 behaviour.

- [ ] **Step 4: Run the tests; expect PASS**, plus every existing test in the project.
  Run: `dotnet test tests/Rag.NET.DataProviders.Web.Tests -c Release`

- [ ] **Step 5: Commit** `fix(web): apply robots.txt groups and longest-match rules per RFC 9309 (#927)`

### Task 2: `*`, `$` and percent-encoding

**Files:**
- Modify: `src/Rag.NET.DataProviders.Web/RobotsTxt.cs`
- Modify: `tests/Rag.NET.DataProviders.Web.Tests/RobotsTxtTests.cs`

**Interfaces:**
- Consumes: Task 1's `Parse` and `IsDisallowed`.
- Produces: `internal static string Normalise(string pathOrPattern)` (private is fine if tests go through `IsDisallowed`).

- [ ] **Step 1: Write the failing tests.**
  - `StarMatchesAnyRun`: `Disallow: /*.pdf` → `/a/b.pdf` and `/a.pdf?x=1` blocked, `/a.html` allowed.
  - `DollarAnchorsTheEnd`: `Disallow: /*.pdf$` → `/a.pdf` blocked, `/a.pdf?x=1` allowed.
  - `DrupalStyleAllow`: `"User-agent: *\nDisallow: /core/\nAllow: /core/*.css$\n"` → `/core/misc/a.css` allowed, `/core/misc/a.js` blocked.
  - `DollarInsideAPattern_IsLiteral`: `Disallow: /a$b` → `/a$b` blocked, `/a` allowed.
  - `LongestMatchCountsPatternOctets`: `"Disallow: /*\nAllow: /page"` → `/page` allowed, `/other` blocked.
  - `PercentEncodedUnreserved_IsDecoded`: `Disallow: /foo/bar/baz` → `/foo/bar/%62%61%7A` blocked; and `Disallow: /foo/bar/%62az` → `/foo/bar/baz` blocked.
  - `PercentEncodedReserved_StaysEncoded`: `Disallow: /a/b` → `/a%2Fb` allowed.
  - `NonAsciiInRule_IsEncoded`: `Disallow: /foo/bar/ツ` → `/foo/bar/%E3%83%84` blocked; lower-case `%e3%83%84` in the rule also blocks it.
  - `EncodedStarInRule_MatchesALiteralStar`: `Disallow: /file-%2A.html` → `/file-*.html` blocked, `/file-x.html` allowed.
  - `ManyStars_FinishQuickly`: `Disallow: /*a*a*a*a*a*a*a*a*b` against `/` + 2,000 `a`s → allowed, and the call completes within 1 second (Stopwatch).

- [ ] **Step 2: Run; expect FAIL** on the wildcard and encoding cases.

- [ ] **Step 3: Implement.** `Normalise`: UTF-8-encode non-ASCII as `%XX`; for each `%XX`, decode it when it is an unreserved character (`A-Z a-z 0-9 - . _ ~`), otherwise upper-case its hex. Apply to the URL's `PathAndQuery` and to each rule value at parse time; a literal `*` in the URL is encoded to `%2A` before comparing. In the rule, a trailing `$` is the end anchor; every other `$` is literal. Match with the iterative two-pointer wildcard walk (remember the last `*` position and the input position it matched from; on mismatch, retry from there one octet later), which is O(n·m) worst case and never recursive. The "most octets" length of a rule is its normalised pattern length, anchor included.

- [ ] **Step 4: Run; expect PASS.**
  Run: `dotnet test tests/Rag.NET.DataProviders.Web.Tests -c Release`

- [ ] **Step 5: Commit** `fix(web): match robots.txt * and $ and normalise percent-encoding (#927)`

### Task 3: Product token from the HttpClient, and Crawl-delay

**Files:**
- Modify: `src/Rag.NET.DataProviders.Web/RobotsTxt.cs`
- Modify: `src/Rag.NET.DataProviders.Web/RequestThrottle.cs`
- Modify: `src/Rag.NET.DataProviders.Web/WebCrawlerDataProvider.cs:44-52`
- Modify: `src/Rag.NET.DataProviders.Web/SitemapDataProvider.cs:103-117`
- Modify: `tests/Rag.NET.DataProviders.Web.Tests/RobotsTxtTests.cs`, `WebCrawlerScopeTests.cs`, `SitemapScopeTests.cs`

**Interfaces:**
- Consumes: Task 1's `Parse`, `CrawlDelay`.
- Produces: `internal static string? ProductTokenOf(HttpClient client)`; `RequestThrottle.RaiseTo(TimeSpan interval)`.

- [ ] **Step 1: Write the failing tests.**
  - `ProductTokenOf_TakesTheFirstProductName`: client with `DefaultRequestHeaders.UserAgent.ParseAdd("MyBot/1.0 (+https://example.com/bot)")` → `"MyBot"`; no header → `null`; `"My.Bot/1.0"` → `null` (invalid token).
  - `UserAgentValueWithVersion_StillNames`: file `User-agent: MyBot/2.0\nDisallow: /b` with token `"MyBot"` → `/b` blocked.
  - `CrawlDelay_IsReadFromTheApplicableGroup`: `"User-agent: *\nCrawl-delay: 5\n\nUser-agent: MyBot\nCrawl-delay: 0.5\n"` → token `"MyBot"` gives `0.5 s`; token `null` gives `5 s`.
  - `CrawlDelay_LargerOfTwoCounts`: two `Crawl-delay` lines `2` and `3` in the applicable group → `3 s`.
  - `CrawlDelay_InvalidIsIgnored`: `Crawl-delay: soon` and `Crawl-delay: -1` → `null`.
  - `RaiseTo_NeverLowers` (in `RobotsTxtTests` or a new `RequestThrottleTests.cs`): `new RequestThrottle(200 ms)` then `RaiseTo(50 ms)`; two `WaitAsync` calls take ≥ 185 ms. `new RequestThrottle(Zero)` then `RaiseTo(150 ms)`; two calls take ≥ 135 ms.
  - `CrawlDelay_SpacesCrawlerFetches` in `WebCrawlerScopeTests`: robots.txt `User-agent: *\nCrawl-delay: 0.15`, `MinimumRequestInterval` zero, seed plus two linked pages → the crawl takes ≥ 2 × 150 ms − 15 ms (lower bound only, as the existing interval test does).
  - `CrawlDelay_SpacesSitemapPageFetches` in `SitemapScopeTests`: the existing interval test's shape, with `RespectRobotsTxt = true`, robots.txt carrying `Crawl-delay: 0.15`, `MinimumRequestInterval` zero → three page opens take ≥ 3 × 150 ms − 15 ms.
  - `CrawlDelay_SmallerThanTheMinimum_DoesNotLowerIt` in `WebCrawlerScopeTests`: `MinimumRequestInterval` 150 ms, `Crawl-delay: 0.01` → still ≥ 2 × 150 ms − 15 ms.

- [ ] **Step 2: Run; expect FAIL.**

- [ ] **Step 3: Implement.** `ProductTokenOf`: first `ProductInfoHeaderValue` with non-null `Product`; its `Name` if it matches `[A-Za-z_-]+`, else `null`. `LoadAsync` calls `Parse(content, ProductTokenOf(httpClient))`. `crawl-delay` parses with `double.TryParse(..., NumberStyles.Float, CultureInfo.InvariantCulture)`; keep the largest non-negative finite value across the applicable groups. `RequestThrottle`: the interval becomes a field updated under the gate (or `Interlocked`), `RaiseTo` keeps the larger value, and `WaitAsync`'s early return reads the current interval. Both providers call `_throttle.RaiseTo(robots.CrawlDelay.Value)` right after `LoadAsync` when it has a value. The sitemap provider's single throttle is shared by all hosts, as the design says.

- [ ] **Step 4: Run; expect PASS.**
  Run: `dotnet test tests/Rag.NET.DataProviders.Web.Tests -c Release`

- [ ] **Step 5: Commit** `fix(web): honour the crawler's robots.txt group and Crawl-delay (#927)`

### Task 4: Documentation

**Files:**
- Modify: `src/Rag.NET.DataProviders.Web/WebCrawlerOptions.cs` (`RespectRobotsTxt`, `MinimumRequestInterval` docs)
- Modify: `src/Rag.NET.DataProviders.Web/SitemapOptions.cs:96-119`
- Modify: `docs/guide/data-providers.md` (the robots.txt paragraph near line 700, and the crawler section's equivalent)
- Modify: `src/Rag.NET.DataProviders.Web/README.md` only if it describes the old rule

- [ ] **Step 1:** Replace "`User-agent: *` group's `Disallow` prefixes" wording with: the group naming the `HttpClient`'s User-Agent product token, else `*`; `Allow`/`Disallow` with `*` and `$`, longest match wins, case-sensitive; `Crawl-delay` raises `MinimumRequestInterval`, never lowers it (one shared interval for all hosts in the sitemap provider). Any C# fence added must compile against the package (pack-validate resolves docs fences).
- [ ] **Step 2:** `dotnet build Rag.NET.slnx -c Release` → 0 warnings, 0 errors.
- [ ] **Step 3: Commit** `docs(web): describe RFC 9309 robots.txt handling and Crawl-delay (#927)`

## Checks

- build: `dotnet build Rag.NET.slnx -c Release`
- tests: `dotnet test Rag.NET.slnx -c Release`
