# Design: ZeroAlloc.ValueObjects Adoption + DocumentSection Type Fix

**Date:** 2026-03-21
**Status:** Approved

---

## Overview

Two complementary improvements to the `DocumentId` type and its usage:

1. **`[ValueObject]` adoption** — replace 6 hand-written equality members on `DocumentId` with source-generated equivalents from `ZeroAlloc.ValueObjects`, reducing the class from ~47 lines to ~27 lines with identical semantics.
2. **`DocumentSection.DocumentId` type fix** — change the field from raw `string` to the typed `DocumentId` wrapper, eliminating a type inconsistency that exists today (compare: `TextChunk.DocumentId` is already typed).

---

## Architecture

`ZeroAlloc.ValueObjects` is a source generator: add `[ValueObject]` to a `partial class` or `partial struct` and it emits `Equals`, `GetHashCode`, `==`, `!=`, and `ToString` at build time — zero allocations, same performance as `record`. The generator is bundled inside the main package under `analyzers/dotnet/cs/`.

By default the generator selects all public properties as equality members. Because `DocumentId` exposes its value via a **private field** (`_value`), the `[EqualityMember]` attribute is used to opt that field in explicitly.

---

## Changes

### `Rag.NET.csproj`

Add package reference (generator is bundled in the main package, registered explicitly following the project's existing `<Analyzer>` convention):

```xml
<PackageReference Include="ZeroAlloc.ValueObjects" Version="1.*" GeneratePathProperty="true" />
<Analyzer Include="$(PkgZeroAlloc_ValueObjects)\analyzers\dotnet\cs\ZeroAlloc.ValueObjects.Generator.dll" />
```

### `Models/DocumentId.cs`

- Add `partial` keyword to class declaration
- Add `[ValueObject]` attribute at class level
- Add `[EqualityMember]` attribute on `_value` field
- Delete: `IEquatable<DocumentId>` interface, `Equals(DocumentId?)`, `Equals(object?)`, `GetHashCode()`, `operator ==`, `operator !=`
- Keep unchanged: `[JsonConverter]`, constructor with validation, `ToString()`, `implicit operator string`, `explicit operator DocumentId`, nested `DocumentIdJsonConverter`

The generated `ToString` is suppressed because `DocumentId` already defines its own `ToString()` returning `_value`. The generator skips generation of any member already present in the partial class.

Equality semantics are preserved: `[EqualityMember]` on a `string` field generates `string.Equals(_value, other._value)` (ordinal, same as the hand-written version) and `_value.GetHashCode()` (ordinal).

### `Models/DocumentSection.cs`

```csharp
// Before
public required string DocumentId { get; init; }

// After
public required DocumentId DocumentId { get; init; }
```

The `implicit operator string` on `DocumentId` means all **read** sites compile without changes. Only **construction** sites that pass a raw `string` are affected.

### Call site updates

**Chunking strategies** (3 files) — `new DocumentId(section.DocumentId)` → `section.DocumentId`:

- `Chunking/FixedSizeChunkingStrategy.cs`
- `Chunking/TokenAwareChunkingStrategy.cs`
- `Chunking/RecursiveChunkingStrategy.cs`

**Parser helper methods** (2 files) — private method parameter type `string documentId` → `DocumentId documentId`:

- `Parsers/MarkdownDocumentParser.cs` — `CreatePlainSection`, `CreateHeadingSection`
- `Parsers.Html/HtmlDocumentParser.cs` — `BuildHeadingSection`, `CreateSection`

All other parsers pass `metadata.DocumentId` (already typed `DocumentId`) — no changes needed.

---

## Testing

No new tests needed. The 10 existing tests in `DocumentIdTests` cover:

- Value equality (`==`, `!=`, `Equals`)
- `GetHashCode` consistency (usable as dictionary key)
- `ToString` returns raw value
- `implicit`/`explicit` operators
- JSON round-trip via custom converter
- Null equality operator behaviour
- Validation (null and empty string throw)

These tests form the regression safety net for the refactor. All 10 must pass after the change.

---

## Out of Scope

- Adopting `[ValueObject]` on other model types — all others are `record` types with generated equality already
- Adding `ZeroAlloc.Analyzers` — separate concern, separate task if desired
- Any changes to `DocumentId`'s public API (operators, constructor, JSON behaviour)
