# The next major: a refusal case and full options for remote clients (#937, #944)

**Date:** 2026-11-22 · **Issues:** #937, #944 · **Release:** 2.0.0

## Intent

Two changes were held for a major release because each breaks callers. #937 adds a `RagError`
case for a deliberate refusal, which breaks exhaustive switches. #944 makes the HTTP and gRPC
clients carry the full `RagOptions`, which changes both wire formats. The decisions are the
operator's comments on the two issues, 2026-11-09 and 2026-11-14. This file records how they were
read where the comments leave a choice.

## #937: `RagError.CallRefused`

- `RagError.CallRefused(CallRefusedException Inner)`, the same shape as `ModelCallFailed`.
- Mapped where a refusal used to become `StorageFailed`: `PipelineIngestor`, `PipelineRetriever`,
  `TagRetriever`, `DeepResearchRetriever`, and the catch-all in `IngestFromProviderAsync` for a
  pipeline that throws a refusal instead of returning it. `DeepResearchRetriever` and
  `FlareAnswerEngine` match the new case where they matched `StorageFailed { Inner: CallRefusedException }`.
- `AskAsync` keeps throwing `InvalidOperationException` with the refusal as `InnerException`
  when retrieval is refused. Only the `RagError` changed; changing what `AskAsync` throws was not
  part of the decision.
- The HTTP API answers `CallRefused` with 429 and the gRPC service with `ResourceExhausted`: a
  spent budget is a quota, not a fault. `ModelCallFailed`, which fell through to a bare 500, now
  answers 502 like the other upstream failures, in the same switch.
- The Service Bus settle policy keeps a refusal transient, as its default arm already did: a
  budget can be replenished before the broker gives up, and a redelivery calls no model.

## #944: full options on the wire

- `AskRequest` and `RetrieveRequest` lose `TopK` and `UseHybridSearch` and gain `Options`. For
  ask it mirrors `RagOptions` with `Retrieval` nested; for retrieve it mirrors `RetrievalOptions`,
  which is what `RetrieveAsync` takes. The proto fields are `optional` and the removed numbers and
  names are `reserved`.
- The server leaves a field it did not receive unset, so the pipeline's own default applies, and a
  request without options passes `null`, as an in-process caller would. The client sends a
  short-form retrieval field only when the caller assigned it, so the in-process rule that
  `Retrieval` and a short-form field do not mix holds on the server. The clients check that rule
  before sending and throw the same `ArgumentException`; the server rejects it too, with 400 or
  `InvalidArgument` naming the fields, for clients that are not ours.
- Streaming is `POST {prefix}/ask/stream` with the ask body. The `GET` endpoint is gone.
- Prompt-shaping fields: the comment names `SystemPrompt`, `Temperature` and
  `ConversationHistory`. The map-reduce and refine prompt templates replace prompt text outright,
  so they are gated with them; leaving them open would let a client do what the gate exists to
  stop. The opt-in is `AllowClientPromptOverrides` on `RagApiOptions` and `RagGrpcApiOptions`,
  off by default.
- What cannot be sent fails instead of being dropped: `RetrievalOptions.Filter` is a
  specification object, code rather than data, and a conversation turn that holds more than text.
  Both throw `NotSupportedException` in the client.
- Metadata filters keep their kind: JSON through `MetadataValue`'s converter, proto through a
  `MetadataValueProto` oneof.
- The clients read `RagOptions.AssignedRetrievalFields` through `InternalsVisibleTo`, as
  `Rag.NET.Security` reads `Copy()`. The shared checks live in `src/Shared/RemoteOptionsGuard.cs`.

## API compatibility

`pack-validate` checks each package against 1.10.4. Only `Rag.NET.Api` and `Rag.NET.Api.Grpc`
break, by the removed request members, and each has a generated `CompatibilitySuppressions.1.x.xml`
that lists exactly those members. The other changed packages only add API. Delete both files
once 2.0.0 is on nuget.org, when `pack-validate` warns about them.

## Tests

- `CallRefused` at each mapping site, the HTTP status and the gRPC status.
- For both clients: round trips of ask, stream and retrieve over a real `TestServer`, compared
  property by property over every public property, so a field a client stops carrying fails;
  defaults parity for `null`, default options and empty wire objects; the conflict and `Filter`
  refusals; and the prompt-shaping gate for every gated field on both ask endpoints.
