# Full retrieval options on the ask path (#932)

**Date:** 2026-11-09 · **Issue:** #932

## Intent

`AskAsync` and `AskStreamingAsync` take `RagOptions`, and `RagPipeline.BuildRetrievalOptions`
(`src/Rag.NET/Pipeline/RagPipeline.cs:130`) copies eight of its fields into a new
`RetrievalOptions`. Every other retrieval option keeps its default. MMR cannot be turned on from the
ask path, and HyDE, multi-query and reranking cannot be turned off per call once they are
registered. `RetrieveAsync` reaches all of them. The rijksoverheid sample hit this: it can only use
the variant its eval picked in chat mode because that variant, dense, is what the defaults give.

## Goals

- A caller of `AskAsync` or `AskStreamingAsync` can pass a complete `RetrievalOptions`.
- New fields on `RetrievalOptions` reach the ask path without anyone editing `RagOptions`.
- Setting both the new property and any of the eight old fields is an error, not a silent choice.

## Non-goals

- Removing or obsoleting the eight `RagOptions` retrieval fields. They stay as the short form.
- Changing the API project's ask endpoint. It builds `RagOptions` from `TopK` and `UseHybridSearch`
  and keeps doing so.
- Changing the rijksoverheid sample's chat mode. Dense, its current variant, needs nothing new.

## Anti-goals

- A caller sets `TopK = 10` and `Retrieval = new() { UseMmr = true }`, and gets five chunks without
  an error.
- A minor release that changes what an existing call does. Every call that doesn't set `Retrieval`
  behaves exactly as before.

## Approaches

1. **`RetrievalOptions? Retrieval` on `RagOptions`** (chosen on #932). When set it is used as is.
   No drift, because the ask path uses the same type `RetrieveAsync` takes. It matches the existing
   `FlareOptions.LookaheadRetrievalOptions`, which also replaces a set of fields as a whole.
2. Mirror the missing fields onto `RagOptions`. This follows the current pattern, but the two types
   drift every time a retrieval option is added. #932 exists because of that drift.

For detecting a conflict, three options were considered:

- **Track assignments** (chosen). Each of the eight setters records that it ran.
- Compare against the defaults. A field set to its default value, such as `TopK = 5`, would be
  ignored without an error.
- Let `Retrieval` win without throwing. Rejected in the #932 decision.

## Design

- `RagOptions` gets `public RetrievalOptions? Retrieval { get; set; }`. It is `null` by default.
- The eight fields become properties with a backing field. Each setter also sets a private flag.
  Object initializers and System.Text.Json both go through the setters, so `TopK = 5` next to
  `Retrieval` counts as a conflict even though 5 is the default.
- `RagPipeline.BuildRetrievalOptions`:
  - When `Retrieval` is `null`, it does what it does today.
  - When `Retrieval` is set and no flag is set, it returns `Retrieval` unchanged.
  - When both are set, it throws `ArgumentException` with `paramName: "options"`. The message names
    the conflicting fields and says to move them into `Retrieval`.
- The throw happens in `AskAsync`, and in `AskStreamingAsync` on the first `MoveNextAsync`, which
  is where that method already validates. It happens before any retrieval or model call.
- `Retrieval`'s own values go through the retriever's existing validation. `TopK = 0` there gives
  the same `ValidationFailed` that it gives from `RetrieveAsync`.
- The `RagOptions` remarks that say MMR and HyDE can't be reached are rewritten to point at
  `Retrieval`. The guide page that documents `RagOptions` gets a short example that turns on MMR
  per call.
- Released as `feat:`, a minor. The change only adds things.

## Left to the builder

- Whether the flags are eight booleans or one `[Flags]` enum.
- Wording of the exception message, as long as it names the fields.
- Which guide page gets the example. Check where `RagOptions` is documented today.

## Tests

- Old fields only: the retriever receives the same `RetrievalOptions` as before. This pins every
  field, so dropping one from the copy fails.
- `Retrieval` only: the retriever receives that exact instance, with `UseMmr`, `UseHyde = false`
  and the rest intact.
- Each of the eight fields set next to `Retrieval`: `ArgumentException`, including a field set to
  its default value. Once for `AskAsync` and once for `AskStreamingAsync`.
- JSON round trip: deserializing `{ "retrieval": { ... } }` sets no flag, and adding `"topK": 5`
  sets one.
