# Archive Parser (ZIP) — Design (Phase 3.10)

**Date:** 2026-07-29
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.10
**Covers:** the **Archive Parser (ZIP)** row in `features.md`, raised while designing Phase 3.9

A `.zip` attachment on an email reaches `EmailAttachmentDispatcher`, matches no registered parser,
logs a warning and yields nothing — the archive's contents never reach the index, and the only signal
is a warning line. That warn-and-skip default is deliberate and stays; zip is simply common enough in
real mail that it should not be one of the misses.

## 0. What this phase actually turned out to be

The roadmap scheduled 3.10 after 3.9 on the grounds that it would *reuse* that phase's traversal
machinery. It cannot, as written: **every piece it needs is `internal` to
`Rag.NET.Parsers.Email`.**

| Type | Purpose | Visibility today |
|---|---|---|
| `EmbeddedMessageContext` | depth + budget carried across the `IDocumentParser` boundary via `DocumentMetadata.Tags` | `internal` |
| `EmbeddedMessageBudget` | the shared remaining allowance, written back through a tag sink | `internal` |
| `MimeTypeMap` | extension → content type | `internal` |
| `EmailAttachmentDispatcher` | select a parser by content type, contain its failures, warn on no match | `internal` |

So the phase is a promotion followed by an addition, not an addition alone.

**The depth/budget one is a security property, not an ergonomic one.** The tags are named
`__rag_email_depth` and `__rag_email_budget`. If an archive parser carries its own separate pair,
then a `zip → .eml → zip` chain has each container type counting only its own levels — and an
attacker who alternates formats walks through two bounds that each look correct in isolation.
Duplicating here would not merely repeat the four-filename-sanitizers debt this repository has
already paid twice; it would introduce a hole.

## 1. Part A — promote the container machinery

Four types move to `Rag.NET.Abstractions`, which is the shared floor every parser package already
references, and where `FileNameSanitizer` already sets the precedent for a shared helper.

| Now | Becomes |
|---|---|
| `EmbeddedMessageContext` | `ContainerContext`, tags `__rag_container_depth` / `__rag_container_budget` |
| `EmbeddedMessageBudget` | `ContainerBudget` |
| `MimeTypeMap` | `ContentTypeMap`, public |
| `EmailAttachmentDispatcher` | `ContainerEntryDispatcher` |

The tag rename is safe. Both keys are stripped from metadata on entry — `ContainerContext.Create`
copies without them — so neither reaches a section, a tag on a stored chunk, or anything persisted.
There is no wire format to migrate.

**The acceptance criterion for Part A is zero behaviour change.** Every existing email test passes,
unmodified. Same posture as Phase 3.9's ordering golden: the refactor is only correct if the tests
written against the old arrangement still hold against the new one.

One invariant must survive the move verbatim, because it is subtle and already documented on
`EmbeddedMessageBudget`: each decrement is written back into the tag dictionary the dispatcher built
for the child, so a parent recovers the count after enumeration. Without it *the cap resets for every
dispatched branch and the real bound becomes `cap ^ depth` rather than `cap`*. `ContainerBudget` keeps
that, and Part B's nested test asserts it across formats rather than trusting it.

## 2. Part B — the zip parser

`Rag.NET.Parsers.Archive`, on `System.IO.Compression` from the BCL — no new third-party dependency.

Each entry is decompressed to memory and dispatched by content type through
`ContainerEntryDispatcher`, which since Phase 3.11 contains a throwing parser to its own entry rather
than failing the whole archive. Directory entries (names ending `/`) and zero-length entries are
skipped. Names compose as `archive.zip#report.pdf`, mirroring the `parent.eml#child.eml` convention.

### What it claims, and what it must not

Claims `application/zip` and `application/x-zip-compressed` — the latter is what older Windows and
IE emit and is common in real mail.

Deliberately **not** claimed:

- `application/epub+zip` — an EPUB is a zip, and `EpubDocumentParser` owns it. A generic zip parser
  answering it would produce entry-by-entry rubbish instead of chapters.
- `application/octet-stream` — Phase 3.11 established that nothing format-specific may answer
  "unknown binary", and made that invariant load-bearing.

Both are now enforced rather than merely intended: Phase 3.11's `ParserClaim` guard fails at startup
if a future change makes this parser claim a type another parser already claims.

## 3. The caps, and why they cannot be read from the archive

`ZipArchiveEntry.Length` comes from the **central directory, which is attacker-controlled**. A bomb
declares whatever size it likes. So the caps are enforced by counting bytes actually read through a
limiting stream wrapper — not by trusting a header, and not by pre-flighting declared sizes.

| Cap | Default | Ceiling |
|---|---|---|
| `MaxTotalUncompressedBytes` | 256 MB | 2 GB |
| `MaxCompressionRatio` | 100:1 | 1000:1 |
| `MaxEntries` | 1,024 | 65,535 |

Options follow `EmailParserOptions`' shape: a caller may lower a limit but not raise it past the
ceiling, and asking for more throws at registration rather than being silently clamped.

**Three caps rather than two, because ratio alone is insufficient.** A 10 GB file of zeros at 1000:1
and a 10 GB file at 2:1 are both fatal, and the second passes any ratio check. Total-bytes catches
what ratio cannot.

`CompressedLength` is the ratio's denominator and is also attacker-controlled — but understating it
makes the computed ratio *higher*, tripping the cap earlier, so the lie fails safe.

### One honest limitation

`ZipArchive.Entries` reads the entire central directory on first access, so the entry-count cap is
checked *after* that read rather than during it. It is bounded by the archive the caller already
holds in a stream, so it is not a new exposure — but it is not streaming either, and a
million-entry directory is read before the count is rejected. Recorded rather than hidden.

## 4. Entry names — naming hygiene, not zip-slip

The roadmap entry lists path traversal in entry names alongside the bombs, as "the classic archive
defect". **That framing overclaims for this parser, and the design corrects it.**

Zip-slip is an *extraction* vulnerability: an entry named `../../etc/passwd` is dangerous because an
extractor writes it to disk. **This parser never touches the filesystem.** Entries are decompressed
into memory and handed to another `IDocumentParser`. A traversal-shaped entry name lands in
`DocumentMetadata.FileName` and nowhere else.

`FileNameSanitizer` is still applied, for exactly the reason it is applied to email subjects — a name
that reaches metadata should be a clean name. But it is recorded as hygiene rather than as a
vulnerability that was closed, because claiming otherwise would leave a future reader believing this
parser was exposed to something it never was.

## 5. Nested containers

`zip → .eml → zip` shares one depth counter and one entry budget, through the promoted tags. That is
the entire justification for Part A, so it is tested directly rather than assumed: a chain that
alternates formats must be bounded by the same numbers as a chain that does not.

## 6. Testing

**Part A:** the existing email suites, unmodified, green. Any edit to an existing email test is a
signal that the promotion changed behaviour and must be reported rather than accommodated.

**Part B:**

- One bomb per cap. Each must fail **before** the corresponding cap exists, and the failure must name
  which cap was hit — a bomb that trips the wrong cap is a test that passes for the wrong reason.
- A low-ratio, high-total archive, which is the case two caps would miss.
- A `zip → eml → zip` chain asserting one shared budget, with the `cap ^ depth` trap asserted
  explicitly: the bound must be `cap`, not `cap` per branch.
- An entry whose parser throws costs only that entry; the archive's other entries survive.
- An `.epub` is **not** claimed by the zip parser, and `application/octet-stream` is not either.
- Declared `ParserClaim`s match `CanParse` exactly — the drift Phase 3.11 left unenforced.

## Out of scope

- **Other archive formats** — 7z, tar, rar. Nothing has asked for them.
- **Encrypted archives.** No password plumbing exists, and inventing it here would be speculative.
- **Changing the warn-and-skip default** for unregistered content types. It is deliberate, and
  Phase 3.11 made it load-bearing.
- **Streaming the central directory.** §3 records why the entry-count cap sits where it does; fixing
  it means not using `ZipArchive`, which is a much larger change for a bounded exposure.
