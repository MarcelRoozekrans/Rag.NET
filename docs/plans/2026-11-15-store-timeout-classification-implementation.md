# Store timeout classification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** During provider ingestion, a cancellation the caller did not request is `TransportFailed` only when the entry's content stream threw it; every other one is `StorageFailed`.

**Architecture:** An internal delegating stream records the first exception a read of the content stream throws. The cancellation catch in `ProcessEntryAsync` compares the caught exception, and its inner-exception chain, against that recorded instance.

**Tech Stack:** .NET, xunit.v3, NSubstitute.

**Spec:** `docs/plans/2026-11-15-store-timeout-classification-design.md`

## Global Constraints

- Starts from main after #1020 merges. Rebase this branch onto it before Task 1.
- No new public API. The stream is `internal sealed`.
- The caller's own cancellation still propagates, never becoming an entry error.
- Commits: conventional, header ≤ 100 characters including the squash suffix ` (#NNNN)`, no nested parentheses in bodies, no Claude trailer or session link. Author with `-c user.name="Marcel Roozekrans" -c user.email=marcel@roozekrans.nl`.

## Review Focus

- A parser swallows a stream fault, then a store times out: `StorageFailed`, not transport. Test in Task 2.
- A parser wraps the stream's cancellation inside another exception that is itself an `OperationCanceledException`: still `TransportFailed`. Test in Task 2.
- The hash-store path reads via `CopyToAsync`: the wrapper's `CopyToAsync` must record the fault too, and the existing `ContentWhoseReadTimesOut_FailsThatEntryAndTheRunGoesOn(withHashStore: true)` must stay green. Covered in Tasks 1 and 2.
- Disposing the wrapper must dispose the content stream, including a `TimeoutBoundedResponseStream` that owns an HTTP response. Test in Task 1.
- A sync read, as the ZIP, OpenXml and MSG parsers do, records the fault. Test in Task 1.

---

### Task 1: `FaultRecordingStream`

**Files:**
- Create: `src/Rag.NET/DataProviders/FaultRecordingStream.cs`
- Create: `tests/Rag.NET.Tests/DataProviders/FaultRecordingStreamTests.cs`

**Interfaces:**
- Produces, in `namespace Rag.NET.DataProviders`:
  - `internal sealed class FaultRecordingStream(Stream inner) : Stream`
  - `public Exception? Fault { get; }` — the first exception any read path threw; later ones do not replace it.
  - `public bool IsFault(Exception ex)` — true when `ex` or any exception in its `InnerException` chain is `Fault` by reference; false when `Fault` is null.

- [x] **Step 1: Write the failing tests** in `FaultRecordingStreamTests`, with an inner stream whose every read path throws a fixed `TaskCanceledException` instance:
  - `EachReadPath_RecordsAndRethrowsTheSameInstance`, a `[Theory]` over `Read(byte[],int,int)`, `Read(Span<byte>)`, `ReadByte()`, `ReadAsync(byte[],int,int,CancellationToken)`, `ReadAsync(Memory<byte>,CancellationToken)`, `CopyTo(Stream,int)`, `CopyToAsync(Stream,int,CancellationToken)`. Assert the thrown exception is the same instance (`Assert.Same`) and `stream.Fault` is that instance.
  - `TheFirstFaultIsKept`: two reads throwing different instances; `Fault` is the first.
  - `IsFault_MatchesTheInstanceOrAnyInnerException_AndNothingElse`: true for the instance, true for `new OperationCanceledException("x", instance)`, false for a different `TaskCanceledException`, false on a stream that never faulted.
  - `CleanReads_PassBytesThroughAndRecordNothing`: over a `MemoryStream` of `"abc"`, `ReadToEnd` through a `StreamReader` gives `"abc"` and `Fault` is null.
  - `Dispose_DisposesTheInnerStream` and `DisposeAsync_DisposesTheInnerStream`.
- [x] **Step 2: Run.** `dotnet test tests/Rag.NET.Tests -c Release --filter "FullyQualifiedName~FaultRecordingStreamTests"`. Expected: build FAILS, type not found.
- [x] **Step 3: Implement** `FaultRecordingStream`. Each overridden read path wraps the inner call in `try { … } catch (Exception ex) { Record(ex); throw; }`, where `Record` uses `Interlocked.CompareExchange` so the first fault wins. Override `CopyTo` and `CopyToAsync` to delegate to the inner stream, so a fault there is recorded too. `CanRead`, `CanSeek`, `Length`, `Position`, `Seek` delegate unrecorded; `CanWrite` is false; `Write`, `SetLength` and `Flush` throw `NotSupportedException` except `Flush`, which is a no-op. `Dispose(bool)` and `DisposeAsync` dispose the inner stream. Add a `<summary>` naming #1019 and why the instance, not a flag, is recorded.
- [x] **Step 4: Run** the same filter. Expected: all PASS.
- [x] **Step 5: Commit** `fix(ingestion): add a stream that records the exception its reads throw`, body `Refs #1019.`

### Task 2: Classify the cancellation by the recorded fault

**Files:**
- Modify: `src/Rag.NET/DataProviders/RagPipelineExtensions.cs` — `ProcessEntryAsync`, its cancellation catch, and the comment block above the method.
- Test: `tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs`, next to `ContentWhoseReadTimesOut_FailsThatEntryAndTheRunGoesOn`.

**Interfaces:**
- Consumes: `FaultRecordingStream(Stream)`, `IsFault(Exception)` from Task 1; the existing `ContentFetchError(Exception, CancellationToken)`.

- [x] **Step 1: Write the failing tests.** Use `RagError.TransportFailed`/`StorageFailed` and `Assert.Same` on `Inner`:
  - `AStoreTimeoutWithoutAHashStore_IsReportedAsStorageFailed`: the pipeline's `IngestAsync` reads the stream to the end, then throws `new TaskCanceledException("store", new TimeoutException())`. Expect `FailedCount == 1`, one `StorageFailed` whose `Inner` is that exception, and a second normal entry still ingested.
  - `AHashStoreTimeout_IsReportedAsStorageFailed`, a `[Theory]` over the hash store's `GetETagAsync` (entry with an ETag) and `SetAsync` throwing a `TaskCanceledException`. Expect `StorageFailed`.
  - `AReadFaultTheParserSwallowed_DoesNotLabelALaterStoreTimeout`: the pipeline catches the stream's `TaskCanceledException` from a first `ReadAsync`, then throws a different `TaskCanceledException`. Expect `StorageFailed` holding the second.
  - `AStreamCancellationWrappedByTheParser_IsStillTransportFailed`: the pipeline catches the stream's exception and throws `new OperationCanceledException("parse", streamException)`. Expect `TransportFailed`.
- [x] **Step 2: Run.** `dotnet test tests/Rag.NET.Tests -c Release --filter "FullyQualifiedName~IngestFromProviderTests"`. Expected: the three `StorageFailed` tests FAIL with `TransportFailed`; the wrapped test passes already; every existing test passes.
- [x] **Step 3: Implement.** In `ProcessEntryAsync`, wrap `rawStream` in a `FaultRecordingStream` right after `OpenContentAsync` and use the wrapper in the `await using` and for both `IngestAsync` and `IngestWithHashCheckAsync`. Declare the wrapper before the outer `try` as `FaultRecordingStream? content = null;` so the catch can see it. Change the cancellation catch to: when the caller's token is not cancelled and `content?.IsFault(ex) == true`, add `ContentFetchError(ex, cancellationToken)`; else when the caller's token is not cancelled, add `new RagError.StorageFailed(ex)`; both return `EntryOutcome.Failed`. Rewrite the first bullet of the comment block above the method to say which cancellation becomes which error and why, and drop the "tracked as #1019" sentence.
- [x] **Step 4: Run** the same filter. Expected: all PASS, including `ContentReadCancelledByTheCaller_StillPropagates`, `ContentFetchCancelledByTheCaller_StillPropagates` and both cases of `ContentWhoseReadTimesOut_FailsThatEntryAndTheRunGoesOn`.
- [x] **Step 5: Run every test project except E2ETests**, each under `timeout 900`, as STATE.md describes. Expected: no new failures.
- [x] **Step 6: Commit** `fix(ingestion): report a store timeout during provider ingestion as StorageFailed`, body `Fixes #1019.`

## Checks

- build: `dotnet build Rag.NET.slnx -c Release`
- tests: `dotnet test Rag.NET.slnx -c Release`
