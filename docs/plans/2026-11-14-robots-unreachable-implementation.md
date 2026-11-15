# Unreachable robots.txt Disallows Its Host — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A robots.txt that answers 5xx or 429, or that cannot be fetched, disallows its whole host. The provider reports that host once as a listing failure, and it parses at most 500 KiB of any robots.txt.

**Architecture:** `RobotsTxt.LoadAsync` sorts each fetch result into parsed, unavailable (allow all) or unreachable (disallow all, carrying a `RagError`). Both web providers yield that error once per host as `Result.Failure`. The existing `IngestFromProviderAsync` path then adds it to `Errors` and does not run full cleanup (#400).

**Tech Stack:** .NET, xUnit v3, `HttpMessageHandler` fakes.

**Spec:** `docs/plans/2026-11-14-robots-unreachable-design.md`

## Global Constraints

- No public API changes. Report with the existing `RagError.HttpFailed` and `RagError.TransportFailed`.
- Unreachable means 5xx, **429**, or no response: `HttpRequestException` without a status, `HttpIOException`, or a `TaskCanceledException` that the caller did not cause. 3xx and every other 4xx mean unavailable, so allow everything.
- Caller cancellation propagates, as today (`FetchFailure.IsSkippable`).
- Parse cap: **512,000 bytes**, which is 500 KiB. When the body is longer, drop everything after the last CR or LF of the part that was read.
- Each error message names the robots.txt URL and says that no page from the host was crawled.
- Commits: conventional, headers of at most 100 characters, no nested parentheses in the body, no Co-Authored-By trailer.
- `HttpClient.GetStringAsync` is banned in this project (`BannedSymbols.txt`). Read through the charset-fallback helper.

## Review Focus

1. **Seed robots.txt unreachable while `SameDomain = false`:** the crawler lists nothing at all. Today the seed's rules gate every host, so disallowing the seed host's robots stops the whole crawl. Expected, and pinned in Task 2.
2. **A sitemap that spans two hosts with one unreachable:** pages from the good host are still listed, and the failure is yielded exactly once. Pinned in Task 3.
3. **A robots.txt over 500 KiB whose last line is cut off:** the shortened rule must not be applied, because it would disallow, or allow, more than the site wrote. Pinned in Task 1.
4. **A 3xx left over after `HttpClient` gives up on redirects:** allow everything, not an outage. Pinned in Task 1.
5. **`RespectRobotsTxt = false`:** no robots fetch and no failure, even when robots.txt would return 503. Pinned in Task 2.

---

### Task 1: Classify the robots.txt fetch and cap it at 500 KiB

**Files:**
- Modify: `src/Rag.NET.DataProviders.Web/RobotsTxt.cs`: `LoadAsync`, `IsDisallowed`, the class doc comment
- Modify: `src/Rag.NET.DataProviders.Web/HttpClientTextExtensions.cs`: add the capped read
- Test: `tests/Rag.NET.DataProviders.Web.Tests/RobotsTxtLoadTests.cs` (new)

**Interfaces:**
- Produces: `internal RagError? Unreachable { get; }` on `RobotsTxt`. When it is not null, `IsDisallowed(url)` returns `true` for every URL and `CrawlDelay` is null.
- Produces: `internal static Task<(string Text, bool Truncated)> GetTextPrefixWithCharsetFallbackAsync(this HttpClient httpClient, string url, int maxBytes, CancellationToken cancellationToken)`. Same charset, BOM and `EnsureSuccessStatusCode` behaviour as `GetStringWithCharsetFallbackAsync`, but it reads at most `maxBytes` bytes of the body.
- `LoadAsync(HttpClient, Uri origin, CancellationToken)` keeps its signature.

- [ ] **Step 1: Write the failing tests** in `RobotsTxtLoadTests`. Use a local handler that maps `https://example.com/robots.txt` to a status code, a body, or a thrown exception.
  - `Status5xxOr429_IsUnreachable` (theory: 500, 503, 429). `Unreachable` is `RagError.HttpFailed` with that status. Its `Content` contains `https://example.com/robots.txt`. `IsDisallowed("https://example.com/")` is true.
  - `Status4xxOr3xx_AllowsEverything` (theory: 404, 401, 403, 410, 302). `Unreachable` is null, and `IsDisallowed("https://example.com/a")` is false.
  - `NoResponse_IsUnreachable` (theory: an `HttpRequestException` with no status, `HttpIOException(ResponseEnded)`, and a `TaskCanceledException` with the caller's token not cancelled). `Unreachable` is `RagError.TransportFailed`. Its `Inner` is an `HttpRequestException` whose `Message` contains the robots URL and whose `InnerException` is the thrown exception.
  - `CallerCancellation_Propagates`: with the token already cancelled, `LoadAsync` throws `OperationCanceledException`.
  - `Over500KiB_DropsThePartialLastLine`: the body is `"User-agent: *\n"`, then `#` comment lines as padding, then `"Disallow: /abcdef\n"` placed so that byte 512,000 falls between `/ab` and `cdef`, then more padding. Assert `IsDisallowed("https://example.com/abz")` is false: the shortened `Disallow: /ab` was not applied. Also assert the handler served more than 512,000 bytes.
  - `Under500KiB_ParsesWholeFile`: a 1 KiB file with its last rule on the final line, no trailing newline, is applied.

- [ ] **Step 2: Run them to see them fail**
  Run: `dotnet test --project tests/Rag.NET.DataProviders.Web.Tests -c Release --filter-class "*RobotsTxtLoadTests"`
  Expected: compile failure. `Unreachable` and the capped read do not exist yet.

- [ ] **Step 3: Implement.**
  - The capped read wraps the response stream so that no more than `maxBytes` bytes reach the `StreamReader`. `Truncated` is true when the body had more.
  - `LoadAsync` calls it with `512_000`. When `Truncated` is set, it cuts the text after the last `\r` or `\n`.
  - In the catch, classify per Global Constraints. Read `HttpRequestException.StatusCode`.
  - Build the error messages:
    - With a status: `$"robots.txt at {robotsUrl} returned {(int)status}. RFC 9309 section 2.3.1.4 reads that as disallow-all, so no page from {origin} was crawled."`
    - With no response: the same text with `could not be fetched` in place of `returned {(int)status}`.
  - Update the `LoadAsync` summary and the class remarks to describe the three outcomes.

- [ ] **Step 4: Run the tests to see them pass**
  Run: the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit**
  `git add src/Rag.NET.DataProviders.Web/RobotsTxt.cs src/Rag.NET.DataProviders.Web/HttpClientTextExtensions.cs tests/Rag.NET.DataProviders.Web.Tests/RobotsTxtLoadTests.cs`
  `git commit -m "fix(web): read an unreachable robots.txt as disallow-all and parse at most 500 KiB, #998"`

### Task 2: The crawler reports an unreachable seed robots.txt

**Files:**
- Modify: `src/Rag.NET.DataProviders.Web/WebCrawlerDataProvider.cs:44-52`
- Modify: `tests/Rag.NET.DataProviders.Web.Tests/WebCrawlerTransientFailureTests.cs:57-73`: replace `AFailedRobotsTxt_AllowsEverything`

**Interfaces:**
- Consumes: `RobotsTxt.Unreachable` from Task 1.

- [ ] **Step 1: Write the failing tests**
  - `AFailedRobotsTxt_ReportsTheHost_AndListsNothing` (theory over the existing `Failures`: `io`, `timeout`). It replaces the old test. The result is exactly one entry, `IsFailure`, with an error of type `RagError.TransportFailed`. No request goes to `https://example.com/`: count the requests and expect only the robots one.
  - `AServerErrorRobotsTxt_ReportsHttpFailed`: robots.txt answers 503. The result is one `RagError.HttpFailed` with `StatusCode == ServiceUnavailable`, and nothing else.
  - `WithRespectRobotsTxtOff_AFailingRobotsTxtIsNeverFetched`: robots.txt would answer 503 and `RespectRobotsTxt = false`. The result is the seed page as a success, with no failure.

- [ ] **Step 2: Run them to see them fail**
  Run: `dotnet test --project tests/Rag.NET.DataProviders.Web.Tests -c Release --filter-class "*WebCrawlerTransientFailureTests"`
  Expected: FAIL. Today the seed page is yielded.

- [ ] **Step 3: Implement.** After `LoadAsync`, if `robots.Unreachable` is not null, `yield return Result<FileEntry, RagError>.Failure(robots.Unreachable)` and `yield break`.

- [ ] **Step 4: Run the tests to see them pass**, together with the whole Web test project:
  `dotnet test --project tests/Rag.NET.DataProviders.Web.Tests -c Release`. Expected: PASS.

- [ ] **Step 5: Commit**
  `git commit -m "fix(web): report the host and crawl nothing when the seed robots.txt is unreachable, #998"`

### Task 3: The sitemap walker reports each unreachable host once; docs

**Files:**
- Modify: `src/Rag.NET.DataProviders.Web/SitemapDataProvider.cs:98-120` (`IsDisallowedByRobotsAsync`) and `:136-185` (`LoadSitemapAsync`, the url loop)
- Modify: `docs/guide/data-providers.md:705` and the crawler paragraph near `:747`
- Modify: `src/Rag.NET.DataProviders.Web/README.md` if it states the failure behaviour (check lines 10-30)
- Test: `tests/Rag.NET.DataProviders.Web.Tests/SitemapScopeTests.cs`, next to `AMissingRobotsTxt_AllowsEverything`

**Interfaces:**
- Consumes: `RobotsTxt.Unreachable` from Task 1.
- `IsDisallowedByRobotsAsync` becomes `Task<RobotsTxt?> RobotsForAsync(string url, Dictionary<string, RobotsTxt> robotsByHost, CancellationToken ct)`. It returns null when `RespectRobotsTxt` is off. `Walk` gets a `HashSet<string> ReportedUnreachable` (origins, case-insensitive).

- [ ] **Step 1: Write the failing tests.** Extend `FakeHttpMessageHandler` with an optional `Dictionary<string, HttpStatusCode> statuses` constructor argument. A URL listed there answers with that status and no body.
  - `AnUnreachableRobotsTxt_ReportsTheHostOnce_AndSkipsItsPages`: a sitemap with three `example.com` URLs and robots.txt answering 503. The result is exactly one failure, `HttpFailed` 503, and no successes.
  - `AnUnreachableHost_DoesNotStopTheOtherHost`: a sitemap with two `example.com` URLs, whose robots.txt answers 503, and two `other.example` URLs, whose robots.txt is missing (404). The result is one failure and both `other.example` URLs as successes.
  - `AMissingRobotsTxt_AllowsEverything` stays as it is (404).

- [ ] **Step 2: Run them to see them fail**
  Run: `dotnet test --project tests/Rag.NET.DataProviders.Web.Tests -c Release --filter-class "*SitemapScopeTests"`. Expected: FAIL.

- [ ] **Step 3: Implement.**
  - In the url loop, get `robots = await RobotsForAsync(...)`.
  - If `robots?.Unreachable` is not null: when `walk.ReportedUnreachable.Add(origin)` succeeds, yield that failure. Then `continue`.
  - Otherwise skip the URL when `robots?.IsDisallowed(loc) == true`.

- [ ] **Step 4: Update the docs.** In `data-providers.md`, replace "A missing or unreachable robots.txt allows everything." with: a missing robots.txt (4xx) allows everything. A robots.txt that answers 5xx or 429, or cannot be fetched, skips its whole host. The run then reports one `HttpFailed` or `TransportFailed` for that host in `ProviderIngestionResult.Errors`, and full cleanup does not run, so an outage does not delete that host's documents. Only the first 500 KiB is read. Make the crawler paragraph near line 747 consistent with this. Fix the README if it says otherwise.

- [ ] **Step 5: Run the full checks below.** Expected: build clean with no warnings, all tests pass.

- [ ] **Step 6: Commit**
  `git commit -m "fix(web): report each unreachable robots.txt host once in the sitemap walker, #998"`

The full-cleanup guarantee needs no new test. `IngestFromProviderTests` (`tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs:535-560`) already proves that a yielded `HttpFailed` stops `CleanupMode.Full` from deleting anything. Tasks 2 and 3 prove that the providers yield one.

## Checks

- build: `dotnet build Rag.NET.slnx -c Release`
- tests: `dotnet test Rag.NET.slnx -c Release`
