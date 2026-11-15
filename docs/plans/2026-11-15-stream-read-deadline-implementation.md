# Stream read deadline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A stalled body in the Airtable and Bitbucket content streams fails that entry within `HttpClient.Timeout`, and the provider run goes on.

**Architecture:** A shared internal `Stream` wrapper in `src/Shared/` bounds each read, sync and async, with an idle deadline of `HttpClient.Timeout`. It owns and disposes the `HttpResponseMessage`. Both connectors return it from `OpenContentAsync`. `RagPipelineExtensions` turns a cancellation the caller did not request into a per-entry `TransportFailed` on the path without a hash store too.

**Tech Stack:** .NET, xunit.v3, NSubstitute.

**Spec:** `docs/plans/2026-11-15-stream-read-deadline-design.md`

## Global Constraints

- The deadline is idle per read, never a total. Its value is the `HttpClient.Timeout` of the client that sent the request; `Timeout.InfiniteTimeSpan` disables it.
- Expiry: `TaskCanceledException` with message `The request was canceled due to the configured HttpClient.Timeout of {timeout.TotalSeconds} seconds elapsing.` and inner `TimeoutException`, the same as `WithBodyDeadlineAsync` in `src/Rag.NET.DataProviders.Web/HttpClientTextExtensions.cs`. The caller's token is never cancelled by it.
- Caller cancellation surfaces as the caller's `OperationCanceledException`, never as the timeout.
- No new public API. The wrapper is `internal` and compiled into each connector through `<Compile Include="..\Shared\..." Link="..." />`.
- Commits: conventional, header ≤ 100 characters, no nested parentheses in bodies, no Claude trailer or session link.

## Review Focus

- `HttpClient.Timeout` set to `Timeout.InfiniteTimeSpan`: reads are never cut off. Test in Task 2.
- The caller cancels during a stalled read: the exception is the caller's, not a timeout. Test in Task 2.
- A non-success status: `HttpRequestException` with the status, as today, and the response disposed. Test in Task 2.
- The hash-store path reads through `CopyToAsync`, so the wrapper's `ReadAsync` override must be what `CopyToAsync` uses. Test in Task 2.
- The pipeline fix must not swallow the caller's own cancellation. Task 1 keeps `ContentFetchCancelledByTheCaller_StillPropagates` green and adds the same case for a read-time cancellation.

---

### Task 1: A non-caller cancellation while ingesting fails one entry

**Files:**
- Modify: `src/Rag.NET/DataProviders/RagPipelineExtensions.cs`. Change the per-entry catch in `ProcessEntryAsync`, about line 441, which excludes every `OperationCanceledException`.
- Test: `tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs`

**Interfaces:**
- Consumes: the existing `ContentFetchError(Exception, CancellationToken)` in the same file.
- Produces: nothing new. Behaviour only.

- [ ] **Step 1: Write the failing tests** next to `ContentThatTimesOut_IsReportedAsTransportFailed`:
  - `ContentWhoseReadTimesOut_FailsThatEntryAndTheRunGoesOn`, a `[Theory]` over `withHashStore` false and true. Use two entries. The first returns a stream whose `ReadAsync` and `Read` both throw `new TaskCanceledException("...HttpClient.Timeout...", new TimeoutException())`; the second is normal text. Assert `result.FailedCount == 1`, `result.IngestedCount == 1`, and that the single error is `RagError.TransportFailed` whose `Inner` is the thrown exception.
  - `ContentReadCancelledByTheCaller_StillPropagates`: the stream's read cancels a linked `cts` and then throws `OperationCanceledException(cts.Token)`. Assert `ThrowsAnyAsync<OperationCanceledException>`, with no hash store.
- [ ] **Step 2: Run them.** Run: `dotnet test tests/Rag.NET.Tests -c Release --filter "FullyQualifiedName~IngestFromProviderTests"`. Expected: the `withHashStore: false` case of the first test FAILS with an escaping `TaskCanceledException`. The `true` case and the caller-cancellation test pass already.
- [ ] **Step 3: Implement.** In `ProcessEntryAsync`, before the catch-all, add a catch for `OperationCanceledException` when `!cancellationToken.IsCancellationRequested` that adds `new RagError.TransportFailed(ex)` and returns `EntryOutcome.Failed`. Reuse `ContentFetchError` where it fits. Update the comment above the catch-all, which says cancellation is excluded, to name this case and #1012.
- [ ] **Step 4: Run** the same filter. Expected: all pass, including `ContentFetchCancelledByTheCaller_StillPropagates`.
- [ ] **Step 5: Commit** `fix(ingestion): fail one entry, not the run, when its content read times out`, with body `Refs #1012.`

### Task 2: The timeout-bounded response stream, used by Airtable

**Files:**
- Create: `src/Shared/TimeoutBoundedResponseStream.cs`
- Modify: `src/Rag.NET.DataProviders.Airtable/Rag.NET.DataProviders.Airtable.csproj`, to add `<Compile Include="..\Shared\TimeoutBoundedResponseStream.cs" Link="Http\TimeoutBoundedResponseStream.cs" />`
- Modify: `src/Rag.NET.DataProviders.Airtable/AirtableDataProvider.cs`, in `DownloadAttachmentAsync` (about line 205)
- Create: `tests/Rag.NET.DataProviders.Airtable.Tests/TimeoutBoundedResponseStreamTests.cs`
- Modify: `tests/Rag.NET.DataProviders.Airtable.Tests/AirtableDataProviderTests.cs`
- Test helper: link `tests/Rag.NET.DataProviders.Web.Tests/StalledBodyContent.cs` into the test project the way `MetadataContract.cs` is linked, or write a local equivalent. Its sync `Read` throws, which the wrapper never calls.

**Interfaces:**
- Produces, in `namespace Rag.NET.DataProviders.Http`:
  - `internal sealed class TimeoutBoundedResponseStream : Stream`
  - `internal TimeoutBoundedResponseStream(HttpResponseMessage response, Stream content, TimeSpan timeout)`
  - `internal static async Task<Stream> OpenAsync(HttpResponseMessage response, TimeSpan timeout, CancellationToken cancellationToken)`: calls `EnsureSuccessStatusCode` and disposes the response if that or `ReadAsStreamAsync` throws, then returns the wrapper. Task 3 uses this.

- [ ] **Step 1: Write the failing wrapper tests** in `TimeoutBoundedResponseStreamTests`. Use a test-local inner stream whose reads can yield bytes after a delay or stall until cancelled. Each test uses a 200 ms timeout unless stated.
  - `AStalledReadAsync_TimesOutAsHttpClientDoes`: `TaskCanceledException`, `InnerException is TimeoutException`, the caller's token not cancelled, message contains `HttpClient.Timeout`.
  - `AStalledSyncRead_TimesOutAsHttpClientDoes`: the same through `Read(byte[], int, int)`.
  - `SlowSteadyReads_CompleteBeyondTheTimeout`: five 1-byte reads, each after 100 ms, with a 300 ms timeout. `CopyToAsync` gets all five bytes.
  - `CopyToAsync_IsBoundedToo`: a stall during `CopyToAsync` times out.
  - `TheCallersCancellation_IsNotReportedAsATimeout`: a 10 s timeout; the caller cancels at 100 ms. The thrown `OperationCanceledException.CancellationToken` is the caller's token and its inner is not `TimeoutException`.
  - `AnInfiniteTimeout_NeverCutsOff`: `Timeout.InfiniteTimeSpan`; a read that yields after 300 ms completes.
  - `Dispose_DisposesTheResponse` and `DisposeAsync_DisposesTheResponse`: use an `HttpResponseMessage` subclass that records `Dispose(bool)`.
  - `OpenAsync_ANonSuccessStatus_ThrowsAndDisposesTheResponse`: a 404 response throws `HttpRequestException` with `StatusCode == NotFound`, and the response is disposed.
- [ ] **Step 2: Run them.** Run: `dotnet test tests/Rag.NET.DataProviders.Airtable.Tests -c Release`. Expected: compile failure, because `TimeoutBoundedResponseStream` does not exist.
- [ ] **Step 3: Implement** the wrapper. It is read-only and not seekable; `Length` and `Position` throw `NotSupportedException`. It overrides `ReadAsync(Memory<byte>, CancellationToken)`, `ReadAsync(byte[], int, int, CancellationToken)`, `Read(byte[], int, int)` and `Read(Span<byte>)`. Each read creates a linked source from the caller's token and calls `CancelAfter(timeout)` unless the timeout is infinite. The sync reads run the inner `ReadAsync` under that token and block on `.AsTask().GetAwaiter().GetResult()`. The exception mapping copies `WithBodyDeadlineAsync`. `Dispose(bool)` and `DisposeAsync` dispose the content and then the response. Add a `<summary>` that names #1012 and says why the deadline is idle and not total: large attachments on slow links.
- [ ] **Step 4: Run** the wrapper tests. Expected: PASS.
- [ ] **Step 5: Write the failing Airtable test** in `AirtableDataProviderTests`: `AStalledAttachmentBody_TimesOutWithinHttpClientTimeout`. Use an `HttpClient` with `Timeout = 200 ms` whose handler returns `StalledBodyContent`. Read the attachment entry's `OpenContentAsync` stream to the end; expect `TaskCanceledException` with an inner `TimeoutException` within 5 s.
- [ ] **Step 6: Run** it. Expected: FAIL. The test hangs until its own 5 s guard, or the read is cancelled by the test's token rather than timing out.
- [ ] **Step 7: Implement.** `DownloadAttachmentAsync` returns `await TimeoutBoundedResponseStream.OpenAsync(response, _http.Timeout, cancellationToken)`. Remove its own `EnsureSuccessStatusCode` and `ReadAsStreamAsync`.
- [ ] **Step 8: Run** the whole Airtable test project. Expected: all pass.
- [ ] **Step 9: Commit** `fix(airtable): bound a stalled attachment body by HttpClient.Timeout per read`, with body `Refs #1012.`

### Task 3: Bitbucket uses the same stream

**Files:**
- Modify: `src/Rag.NET.DataProviders.Bitbucket/Rag.NET.DataProviders.Bitbucket.csproj`, with the same `<Compile ... Link>` as Task 2
- Modify: `src/Rag.NET.DataProviders.Bitbucket/BitbucketDataProvider.cs`, in `GetRawFileStreamAsync` (about line 192)
- Modify: `tests/Rag.NET.DataProviders.Bitbucket.Tests/BitbucketDataProviderTests.cs`, plus the same stalled-content helper as Task 2

**Interfaces:**
- Consumes: `TimeoutBoundedResponseStream.OpenAsync(HttpResponseMessage, TimeSpan, CancellationToken)` from Task 2.

- [ ] **Step 1: Write the failing test** `AStalledRawFileBody_TimesOutWithinHttpClientTimeout`. Build the provider with a raw `HttpClient` whose `Timeout` is 200 ms and whose handler answers the `src/` URL with `StalledBodyContent`. Assert as in Task 2, Step 5.
- [ ] **Step 2: Run** `dotnet test tests/Rag.NET.DataProviders.Bitbucket.Tests -c Release`. Expected: the new test FAILS.
- [ ] **Step 3: Implement.** `GetRawFileStreamAsync` returns `await TimeoutBoundedResponseStream.OpenAsync(response, _http.Timeout, ct)`.
- [ ] **Step 4: Run** the Bitbucket test project. Expected: all pass.
- [ ] **Step 5: Commit** `fix(bitbucket): bound a stalled raw-file body by HttpClient.Timeout per read`, with body `Fixes #1012.`

## Checks

- build: `dotnet build Rag.NET.slnx -c Release`
- tests: `dotnet test Rag.NET.slnx -c Release`
