# Store timeout classification during provider ingestion — design

**Issue:** #1019. **Status:** approved 2026-11-15. **Size:** small change. **Builds on:** #1020 (#1012).

## Intent

#1020 made `ProcessEntryAsync` fail one entry, rather than the whole provider run, on an
`OperationCanceledException` the caller did not request, and labels it `TransportFailed`. That
label is right for a content stream that stalls. The same catch also takes a timeout from the
vector store, BM25, the content-hash store or a remote `IRagPipeline` client, and labels those
`TransportFailed` too, although every other exception from them is `StorageFailed`. An operator
reading `TransportFailed` inspects the source connector instead of the store that timed out.

## Goals

- A cancellation thrown by a read of the entry's content stream stays `TransportFailed`.
- Any other cancellation the caller did not request fails the entry as `StorageFailed`.
- The caller's own cancellation still ends the run, unchanged.
- No new public API; store implementations outside this repo are covered too.

## Non-goals

- Per-store timeout translation, a new public exception type, or a new `RagError` case.
- Changing the labels of non-cancellation exceptions.
- Changing how `IngestWithHashCheckAsync` classifies its buffer copy, which is already right.

## Approaches

| Approach | Trade-off |
|---|---|
| **Record the stream's fault** — picked by the operator | One file; covers every store, including third-party ones. Needs a delegating stream. |
| Stores map their own timeouts, as `EmbeddingBehavior` does with `ModelCallException` | New public exception; a change in 7 vector-store packages, the SQLite hash store and both pipeline clients; third-party stores stay mislabelled. |
| Both | The second half has the same cost, for one extra distinction nobody asked for. |

## Design

Only the path without a hash store is ambiguous. With a hash store, `IngestWithHashCheckAsync`
copies the content into a buffer before anything else runs, and a failed copy is already
classified there; any cancellation reaching the outer catch on that path comes from a store or
the pipeline.

1. **`FaultRecordingStream`**, internal to `Rag.NET`, beside `RagPipelineExtensions`. A
   read-only delegating `Stream` over the entry's content stream that owns and disposes it. Every
   read path that can reach the inner stream — `Read(byte[]…)`, `Read(Span)`, `ReadAsync`
   (both overloads), `ReadByte`, `CopyTo`, `CopyToAsync`, `BeginRead`/`EndRead` through the base
   — catches any exception, stores the first one in a `Fault` property, and rethrows it unchanged
   with `throw;`. `CanRead`/`CanSeek`/`Length`/`Position`/`Seek` delegate; writes throw
   `NotSupportedException` as for any read-only stream. Seek and length failures are not
   recorded: they are not a stalled download.
2. **`ProcessEntryAsync`** wraps `rawStream` in it right after `OpenContentAsync` and disposes
   the wrapper instead.
3. **The cancellation catch** reports `TransportFailed` only when the caught exception is the
   recorded fault, or has it somewhere in its `InnerException` chain (a parser may wrap it).
   Matching the instance rather than a "some read failed" flag keeps a read error the parser
   handled, followed by a store timeout, from being labelled transport.
4. **Otherwise** a cancellation the caller did not request becomes `StorageFailed`, the same
   label the catch-all gives every other exception from these components. The caller's
   cancellation still propagates.
5. The note above `ProcessEntryAsync` drops its "tracked as #1019" sentence and describes the
   split.

## Tests

In `tests/Rag.NET.Tests/DataProviders/`:

- No hash store, content stream whose read throws `TaskCanceledException` around
  `TimeoutException`: entry fails, error is `TransportFailed` holding that exception, run goes on.
- No hash store, pipeline whose `IngestAsync` throws a `TaskCanceledException` after reading the
  stream cleanly: `StorageFailed`.
- No hash store, stream read faults once and the parser swallows it, then the pipeline throws a
  different cancellation: `StorageFailed`.
- A parser wrapping the stream's cancellation in another `OperationCanceledException`:
  `TransportFailed`.
- Hash store whose `GetETagAsync` or `SetAsync` times out: `StorageFailed`.
- Caller cancels: the run ends with `OperationCanceledException`, as before.
- `FaultRecordingStream` unit tests: each read overload records and rethrows the same instance;
  disposing it disposes the inner stream.
