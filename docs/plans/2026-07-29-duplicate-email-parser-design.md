# Duplicate Email Parser — Design (Phase 3.11)

**Date:** 2026-07-29
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.11
**Covers:** the duplicate-parser defect found in the Phase 3.9 whole-phase review

Runs ahead of 3.10 despite the higher number. This is a live defect that fails a whole document
parse; 3.10 is new capability.

## 1. The defect

> **Corrected during implementation.** This section originally presented `MimeTypeMap` as *the*
> source of `application/octet-stream`, and Task 1's instructions repeated it. That is true only for
> `.msg`. `MimeTypeMap.FromFileName` has exactly one caller in the repository —
> `StorageMessageAdapter.cs:86` — and the `.eml` path never touches it: `MimeMessageAdapter.Enumerate`
> builds the type from the attachment's own MIME headers
> (`$"{attachment.ContentType.MediaType}/{attachment.ContentType.MediaSubtype}"`). So an `.eml`
> attachment is `application/octet-stream` because the sending mail client wrote that into the part
> headers, which is both commoner and less avoidable than a lookup-table miss. The defect and the fix
> are unchanged; the causal story below is narrower than it should have been, and §6 of the plan is
> corrected so the `MimeTypeMap` XML does not inherit the mis-attribution.

`MimeTypeMap.FromFileName` returns `application/octet-stream` for any extension it does not know,
and `MimeTypeMap`'s own type XML documents the consequence:

> unknown extensions map to `application/octet-stream`, which no parser claims, so those attachments
> are skipped with a warning by `EmailAttachmentDispatcher`.

Two parsers claim it. Both live in `Rag.NET.Chunking.Templates`:

- `EmailDocumentParser.CanParse` accepts `message/rfc822` **and** `application/octet-stream`
- `QAPairsDocumentParser.CanParse` accepts `text/csv`, two spreadsheet types **and**
  `application/octet-stream`

With `UseEmailChunking()` or `UseQAPairsChunking()` registered alongside `AddEmailParser()`, one
`.eml` carrying a single `payload.dat` throws `InvalidOperationException` out of the **entire**
document parse — body, headers and every other attachment lost with it. `EmailAttachmentDispatcher`
has no `try`/`catch` around `parser.ParseAsync`, so the exception escapes to the caller.

Both parsers throw by different routes: `EmailDocumentParser` wraps a MimeKit failure, and
`QAPairsDocumentParser` reaches `Cannot resolve question/answer columns` and throws there. Neither
degrades.

This breaks the email parser's documented "degrades rather than breaks" contract, on a registration
combination a user has no reason to suspect is dangerous.

### It is not confined to email attachments

Top-level ingestion selects the same way. `ParseBehavior` uses
`Parsers.FirstOrDefault(p => p.CanParse(contentType))` and `ParentDocumentIngestionBehavior` uses
`Parsers.First(...)`, which throws when nothing matches. Any document typed
`application/octet-stream` reaches the same coin-flip.

### The interface cannot express the correct rule

`IDocumentParser.CanParse(string contentType)` receives only the content type — never the file name.
"Accept `application/octet-stream` only when the name ends `.eml`" is therefore not expressible at
the point the decision is made. Both parsers already work around this *inside* `ParseAsync`:
`QAPairsDocumentParser` branches on `.xlsx`/`.xls` read from `metadata.FileName`.

Widening the interface was considered and rejected for this phase: it is a breaking change across
every parser in the repository, and the narrow fix does not depend on it. Recorded here so the
option is visibly declined rather than unnoticed.

## 2. Remove the claim

Drop `application/octet-stream` from both `CanParse` implementations in `Rag.NET.Chunking.Templates`.
Four lines.

`application/octet-stream` means "unknown binary". A format-specific parser must not answer it — the
answer is always a guess, and a wrong guess here is an exception rather than a miss.

The cost is real and small: a genuinely untyped `.eml` or `.csv` that one of these parsers happened
to win today stops being parsed, and the documented behaviour is warn-and-skip.

> **Corrected during implementation.** This paragraph originally said which parser won was
> "registration-order roulette". It is not, for *this* defect — it is deterministic in both orders,
> and both were measured failing identically. `Rag.NET.Parsers.Email`'s parsers decline
> `application/octet-stream` outright, so the dispatcher's linear search falls through to the
> Templates parser whichever order the user registered in; and `AddRagNet` calls
> `AddRagNETServices()` *before* `configure?.Invoke(builder)`, so the built-in text and markdown
> parsers always precede anything the user registers and user order only permutes the tail.
> **Registering `AddEmailParser()` first is not a workaround**, and nothing here should be read as
> implying it might be. The genuine order-dependence is on `message/rfc822`, where both packages do
> claim — §4.

## 3. Contain attachment failures

Removing the claim fixes this parser. It does not stop the next parser that accepts a type and then
throws — including a third-party one.

`EmailAttachmentDispatcher` wraps `parser.ParseAsync` in a `try`/`catch`, logs a warning naming the
attachment and the parser type, and continues with the next attachment.

**Top-level ingestion keeps throwing.** The asymmetry is deliberate:

- An **attachment** is sub-content the caller never asked for by name. Losing the body and nine good
  attachments to one bad `payload.dat` is exactly the contract violation this phase exists to fix.
- A **document the caller passed directly** should fail loudly. Silently indexing nothing, and
  leaving the caller to discover it in logs, is worse than an exception.

## 4. Make the remaining overlap loud

> **Corrected during implementation. §4 and §6 were mutually exclusive as written.** This section
> makes "both packages registered" an illegal configuration. §6 makes that same configuration the
> phase-defining test — "both packages registered, one `.eml` carrying one `payload.dat`, asserting
> the body and the other attachments survive… that test is the phase". Both cannot stand. Adding
> `ValidateParserClaims` turned Task 1's own end-to-end test red, and neither section notices the
> other exists.
>
> Underneath was a defect this design did not see at all. The error message below tells the user to
> "register only one of them", but `UseEmailChunking()` registers a **parser and a chunking
> strategy**, unconditionally. There was no way to take the email chunking strategy without also
> taking its parser, so the instruction asked the user to do something the API did not permit. The
> conflict is only ever about the parser; the strategy consumes `DocumentSection`s and does not care
> which parser produced them.
>
> **Resolved by making the parser optional.** `EmailChunkingOptions.RegisterParser` (default `true`)
> and the same flag on `QAPairsChunkingOptions`: when `false`, the registration adds the chunking
> strategy and its options but neither the parser nor its `ParserClaim`. The flag is on both because
> the guard fires on *any* duplicated claim — a third-party CSV parser included — and an escape
> hatch present on one bundling registration and absent from its twin is a trap of its own. The
> `ParserClaim` carries the opt-out so the message can quote it verbatim rather than `AddRagNet`
> knowing anything about the packages that collide; `AddEmailParser()` registers nothing but parsers,
> declares none, and is offered none.
>
> The combination this makes reachable — email-shaped chunking with `Rag.NET.Parsers.Email` doing
> the parsing — is the one a user would actually want, and it was unreachable before this phase.
> §6's end-to-end test was deleted rather than repaired: it asserted behaviour in a configuration
> this phase declares invalid. What replaced it, and what it cost, is in §6's own correction below.

After §2, both parsers still claim `message/rfc822`. Registration order decides which wins, and when
the Templates parser wins, a 3-level nested `.eml` yields **2 sections instead of 6** — measured, not
theorised. Silent content loss.

This phase does not pick a winner. The two parsers serve different purposes: the Templates one
produces header/body/attachment-text sections shaped for `EmailChunkingStrategy`, and
`Rag.NET.Parsers.Email` does recursion, depth accounting and attachment dispatch. Which one a user
wants is a question only that user can answer.

So the conflict becomes a **startup error** naming both parsers and both registration calls.

### Why detection needs a declared claim

`AddRagNet` already has the right hook point — `configure?.Invoke(builder)` followed by
`WireRefinementStrategy`, `WireDeepResearch`, `WireTimeWeighting`, `WireTagRetrieval` — which sees
the final registration set regardless of the order the user called things in.

What it cannot do there is ask the parsers what they claim:

- Calling `CanParse` needs live instances, which means building a service provider during
  registration — early instantiation of singletons, with the double-construction problems that
  brings.
- Reading `ServiceDescriptor.ImplementationType` does not work either: every colliding registration
  uses a factory lambda (`sp => sp.GetRequiredService<EmailDocumentParser>()`), so
  `ImplementationType` is `null` and only `ImplementationFactory` is set.

So each registration site declares what it claims — a small `ParserClaim(ContentType, ParserTypeName,
RegistrationMethod)` singleton added alongside the parser. `ValidateParserClaims(services)` compares
declared claims and throws on overlap. Order-independent, no resolution, no instantiation, and the
error message can name the actual registration calls a user would recognise.

### The limit, stated rather than papered over

This catches first-party registrations that declare claims. A third-party parser registered through
`AddParser<T>()` declares nothing and will not be detected. `CanParse` is a *predicate*, not an
enumeration, so nothing can discover what an arbitrary parser accepts without probing it against a
guessed list of content types. That is a worse mechanism than an undetected third-party collision.

## 5. Rename

`Rag.NET.Chunking.Templates.EmailDocumentParser` → `EmailTemplateDocumentParser`, with the file
renamed to match (MA0048 requires it). Two public types with the same name, both `IDocumentParser`,
both claiming `message/rfc822`, is a needless reading hazard in stack traces and logs.

A public API break, free now and expensive after 4.1 ships packages.

## 6. Testing

> **Corrected during implementation.** The end-to-end case named below is a configuration §4 makes
> illegal — see the correction there. `ParserClaimConflictTests` was deleted rather than repaired,
> and two things replaced it.
>
> `EmailChunkingWithoutItsParserTests` covers the configuration that is now legal:
> `UseEmailChunking(o => o.RegisterParser = false)` alongside `AddEmailParser()`. It asserts the
> pairing builds, that the chunking strategy survives the opt-out, and — by driving a real `.eml`
> with an embedded message through the resolved parser — that `Rag.NET.Parsers.Email`'s parser is
> the one an `.eml` actually reaches, since only that parser descends.
>
> The `application/octet-stream` regression moved to `QAPairsAttachmentClaimTests`, whose
> registration (`UseQAPairsChunking()` + `AddEmailParser()`) shares no content type and stays legal.
> **That test as first written did not pin the regression at all.** Restoring the
> `application/octet-stream` clause in `QAPairsDocumentParser.CanParse` left both of its
> section-level assertions green, because §3's containment means a parser that wrongly claims a type
> and then throws produces sections identical to no parser claiming it: the body and the siblings
> survive either way. §3 and §2 were designed independently and nobody noticed that the first hides
> the symptom the second's test was watching for. The two states differ only in the dispatcher's log
> line — `NoParserForAttachment` versus `AttachmentParserFailed` — so the test now asserts the first
> and the absence of the second, and was confirmed red against the reverted fix. The general lesson
> is worth more than the fix: **a containment mechanism added in the same phase as a routing fix can
> silently make the routing fix untestable through its original symptom.**

**The end-to-end case first, and it must fail before the fix:** both packages registered, one `.eml`
carrying one `payload.dat`, asserting the body and the other attachments survive. That test is the
phase — everything else supports it.

Then one test per fix:

- `.csv` typed `text/csv` still reaches `QAPairsDocumentParser`, and `.eml` typed `message/rfc822`
  still reaches a parser. **The fix must not narrow the legitimate claims** — this is the guard
  against over-correcting.
- A parser that throws mid-attachment loses only that attachment; siblings and body survive, and the
  warning names the attachment and the parser.
- Top-level ingestion of a document whose parser throws still throws, in both registration orders.
- Registering both packages throws at startup, and the message names both parsers and both
  registration methods. **Assert the names, not the prose** — a Phase 3.9 test pinned prose and
  passed against a mutant because a second incidental occurrence of the literal satisfied it.

## Out of scope

- **Merging the two parsers**, or deciding which should own `message/rfc822`. They serve different
  purposes; the startup error asks the user.
- **Changing `IDocumentParser`** to pass the file name into `CanParse`. Considered in §1, declined.
- **Containment at top-level ingestion.** Deliberate asymmetry, §3.
- **Detecting third-party parser collisions.** §4 states why it is not mechanisable.

## Cost

The defect is four lines. §3–§5 are roughly 150 lines of new machinery whose only job is to make a
misconfiguration loud instead of silent. That is the trade this design makes deliberately: the same
misconfiguration currently costs two-thirds of an email's content with nothing in the logs to say so.
