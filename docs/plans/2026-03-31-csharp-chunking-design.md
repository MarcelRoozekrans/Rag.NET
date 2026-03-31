# C# Semantic Chunking (Roslyn) — Design

**Date:** 2026-03-31
**Status:** Approved

## Overview

Add `Rag.NET.Chunking.CSharp` — a new package that implements `IChunkingStrategy` by parsing C# source files with Roslyn (`Microsoft.CodeAnalysis.CSharp`). Each top-level and nested member (class, method, property, interface, etc.) becomes its own `TextChunk`, carrying C#-specific metadata in `TextChunk.Metadata`.

## Motivation

`CodeChunkingStrategy` in `Rag.NET.Chunking` uses regex separator hierarchies — it splits on `\npublic class ` and `\nnamespace ` patterns. This regularly misses members, splits mid-method on comments, and captures no structured metadata. Roslyn gives exact AST boundaries, full namespace resolution, XML doc extraction, and accessibility filtering.

## Package

**`Rag.NET.Chunking.CSharp`**

| File | Purpose |
|---|---|
| `CSharpChunkingOptions.cs` | Options: private/internal member inclusion, body inclusion |
| `CSharpChunkingStrategy.cs` | `IChunkingStrategy` implementation using Roslyn |
| `RagBuilderExtensions.cs` | `UseCSharpChunking(Action<CSharpChunkingOptions>?)` on `IRagBuilder` |

**`tests/Rag.NET.Chunking.CSharp.Tests/`**

Unit tests — no real files, all inputs are inline C# strings.

## Interface

Implements `IChunkingStrategy` only:

```csharp
IAsyncEnumerable<TextChunk> ChunkAsync(
    DocumentSection section,
    ChunkingOptions options,
    CancellationToken cancellationToken = default);
```

`IDocumentChunkingStrategy` is not implemented — Roslyn operates per-file, which maps to per-section. No cross-file context is needed.

## Algorithm

1. Parse `section.Text` with `CSharpSyntaxTree.ParseText()`
2. Walk the root `CompilationUnitSyntax` with a `CSharpSyntaxWalker`
3. For each member declaration node, extract:
   - Full source text of the node (via `node.ToFullString()`)
   - Namespace (walk up ancestors for `NamespaceDeclarationSyntax` / `FileScopedNamespaceDeclarationSyntax`)
   - Containing type name (nearest enclosing `TypeDeclarationSyntax`, if any)
   - Member kind (class, method, property, etc.)
   - Member name (`Identifier.Text`)
   - Accessibility modifier
   - XML doc `<summary>` text (stripped of tags)
4. Yield one `TextChunk` per qualifying member
5. Members exceeding `ChunkingOptions.MaxChunkSize` are yielded as-is with `csharp.oversized = "true"`

**Member node types that become chunks:**
`ClassDeclaration`, `InterfaceDeclaration`, `RecordDeclaration`, `StructDeclaration`, `EnumDeclaration`, `MethodDeclaration`, `ConstructorDeclaration`, `PropertyDeclaration`, `DelegateDeclaration`, `EventDeclaration`

Nested types each produce their own chunk (not merged into the containing type chunk).

## Options

```csharp
public sealed class CSharpChunkingOptions
{
    /// <summary>Include private members. Default: false.</summary>
    public bool IncludePrivateMembers { get; init; } = false;

    /// <summary>Include internal members. Default: true.</summary>
    public bool IncludeInternalMembers { get; init; } = true;

    /// <summary>
    /// Include member bodies. When false, only the signature and XML doc are included.
    /// Useful for reducing chunk size when body content is not needed for retrieval.
    /// Default: true.
    /// </summary>
    public bool IncludeBodies { get; init; } = true;
}
```

## Metadata Keys

All keys use a `csharp.` prefix. Future Tree-sitter packages follow the same `<language>.` convention.

| Key | Example | Notes |
|---|---|---|
| `csharp.kind` | `"method"` | `class`, `interface`, `record`, `struct`, `enum`, `method`, `constructor`, `property`, `delegate`, `event` |
| `csharp.namespace` | `"Rag.NET.Chunking"` | Empty string if no namespace |
| `csharp.type` | `"CSharpChunkingStrategy"` | Containing type name; empty for top-level types |
| `csharp.name` | `"ChunkAsync"` | Member identifier |
| `csharp.accessibility` | `"public"` | `public`, `internal`, `protected`, `private`, `protected internal`, `private protected` |
| `csharp.summary` | `"Splits C# source..."` | XML doc summary text, tags stripped. Empty if no doc comment. |
| `csharp.oversized` | `"true"` | Present only when chunk exceeds `MaxChunkSize` |

## Error Handling

| Scenario | Behaviour |
|---|---|
| Roslyn parse errors (invalid C#) | Log warning; yield the whole section as one chunk (graceful degradation) |
| Empty / whitespace section | Return empty async enumerable — no chunks |
| Member exceeds `MaxChunkSize` | Yield as-is; add `csharp.oversized = "true"` metadata |
| No members found | Return empty async enumerable |

## Registration

```csharp
// Default options
services.AddRagNet(rag => rag.UseCSharpChunking());

// Custom options
services.AddRagNet(rag => rag.UseCSharpChunking(o =>
{
    o.IncludePrivateMembers = true;
    o.IncludeBodies = false;
}));
```

Registers `IChunkingStrategy` as `CSharpChunkingStrategy` singleton.

## Testing

| Test | What it verifies |
|---|---|
| `ChunkAsync_SimpleClass_YieldsOneChunkPerMember` | Method + property → 2 chunks |
| `ChunkAsync_NestedClass_YieldsOuterAndInnerSeparately` | Nested types each get their own chunk |
| `ChunkAsync_XmlDoc_ExtractedToSummaryMetadata` | `<summary>` text appears in `csharp.summary` |
| `ChunkAsync_PrivateMember_ExcludedByDefault` | Private method not yielded with default options |
| `ChunkAsync_PrivateMember_IncludedWhenOptionSet` | Private method yielded when `IncludePrivateMembers = true` |
| `ChunkAsync_ParseError_YieldsWholeSection` | Invalid C# → single fallback chunk, no exception |
| `ChunkAsync_EmptyInput_ReturnsEmpty` | Empty string → no chunks |
| `ChunkAsync_MetadataKeys_CorrectNamespaceAndType` | Namespace + containing type metadata accurate |
| `UseCSharpChunking_RegistersIChunkingStrategy` | DI: `IChunkingStrategy` resolves as `CSharpChunkingStrategy` |

## Future: Tree-sitter

When `Rag.NET.Chunking.TreeSitter` is built:
- Metadata keys follow the same `<language>.kind`, `<language>.namespace` (where applicable), `<language>.name`, `<language>.accessibility` convention
- `CSharpChunkingStrategy` stays untouched — no shared internal abstraction
- The Tree-sitter package registers its own `IChunkingStrategy` and can co-exist or replace the Roslyn one

## Dependencies

| Package | Version |
|---|---|
| `Microsoft.CodeAnalysis.CSharp` | latest stable |
| `Rag.NET.Abstractions` | project reference |
