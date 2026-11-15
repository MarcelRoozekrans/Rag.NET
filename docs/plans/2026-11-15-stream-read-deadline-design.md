# Stream read deadline for Airtable and Bitbucket — design

**Issue:** #1012. **Status:** approved 2026-11-15. **Size:** small change.

## Intent

`AirtableDataProvider.DownloadAttachmentAsync` and `BitbucketDataProvider.GetRawFileStreamAsync`
send with `HttpCompletionOption.ResponseHeadersRead` and return the live response stream.
`HttpClient.Timeout` stops at the headers, so a server that sends headers and then stalls holds
the ingest read until the caller's token fires, or forever. #1009/#1016 fixed the same flaw for
the web provider, where the body is read inside the provider; here the stream leaves the
provider, so the deadline has to travel with it.

## Goals

- A stalled body read in either connector fails that entry within `HttpClient.Timeout`.
- A slow but steady download of a large attachment or file still completes.
- The failure is reported as a transport failure for that entry, and the provider run goes on.
- The `HttpResponseMessage` is disposed with the stream.

## Non-goals

- Changing the web provider's total deadline from #1016.
- New configuration: the limit is the client's existing `HttpClient.Timeout`.
- Buffering bodies into memory.

## Approaches

| Approach | Trade-off |
|---|---|
| **Idle deadline per read** — picked by the operator | Each read must return bytes within `HttpClient.Timeout`. Cuts off a stall; never cuts off a healthy large download. |
| Total deadline from the request, as #1016 | Consistent with the web provider, but fails a large file on a slow link that never stalled. |
| Both idle and total | Needs a second number that no option supplies today. |

For the expiry the operator picked: throw like `HttpClient` — `TaskCanceledException` around
`TimeoutException`, the caller's token untouched — and fix the pipeline so such a cancellation
fails one entry. The alternative, an `IOException`, would need no pipeline change but labels the
entry `StorageFailed` and leaves the pipeline gap below in place.

## Design

**`TimeoutBoundedResponseStream`** (my call on the name), a read-only `Stream` in
`src/Shared/`, linked into both connector projects with `<Compile Include ... Link>` like
`GraphErrorMapping.cs`. No public API, no cross-package `InternalsVisibleTo`.

- Built from the `HttpResponseMessage`, its content stream, and `HttpClient.Timeout`.
  An infinite timeout disables the limit.
- Every async read (`ReadAsync` for `Memory<byte>` and `byte[]`, so `CopyToAsync` goes through it)
  runs under a token linked to the caller's token and cancelled after the timeout. The timer
  starts per call, so only time without progress counts.
- On expiry with the caller's token not cancelled: throw `TaskCanceledException` with the
  `HttpClient.Timeout` message, wrapping `TimeoutException`, as `WithBodyDeadlineAsync` does.
- The sync `Read` gets the same limit (my call). `ZipArchive`, OpenXml (`WordprocessingDocument`,
  `SpreadsheetDocument`) and MsgReader read synchronously, and without a hash store they receive
  this stream directly. It runs the inner async read under the deadline token and blocks on it
  (`ConfigureAwait(false)`, so there's no context to deadlock on). This is sync-over-async, but
  the inner HTTP stream blocks a thread on its sync `Read` anyway.
- `Dispose`/`DisposeAsync` dispose the content stream and the response.
- Both connectors: `EnsureSuccessStatusCode` as now, but dispose the response on failure, then
  return the wrapper.

**Pipeline fix** in `RagPipelineExtensions`: in the per-entry path, an
`OperationCanceledException` thrown while the caller's token is not cancelled fails that entry
as `RagError.TransportFailed`, matching what `ContentFetchError` already does on the hash-store
path. Today, without a hash store, `PipelineIngestor` rethrows it and the per-entry catch skips
it, so it ends the whole provider run. The caller's own cancellation still propagates. The catch is deliberately broad: it also
covers a vector-store, hash-store or remote-pipeline client timeout. Model calls never get
there; they are already `ModelCallFailed`. A store timeout is labelled `TransportFailed` rather
than `StorageFailed`, which is tracked as #1019.

## Tests

- Wrapper: a stalled read times out as `TaskCanceledException` around `TimeoutException`,
  caller token not cancelled, through both `ReadAsync` and sync `Read`; slow steady reads that each beat the limit complete; caller
  cancellation surfaces as the caller's; dispose disposes the response.
- Each connector: a stalled body from its `OpenContentAsync` times out. Reuse the approach of
  `StalledBodyContent` from `Rag.NET.DataProviders.Web.Tests` with a short `HttpClient.Timeout`.
- Pipeline: without a hash store, a stream that throws a non-caller cancellation fails one
  entry as `TransportFailed` and the run ingests the remaining entries; caller cancellation still
  ends the run.

## Left to the builder

The test project layout for the shared wrapper. It may be tested through one connector's test
project or a linked copy.
