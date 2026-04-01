# Mind-Map Extractor — Design

**Package:** `Rag.NET.GraphRag`
**Date:** 2026-04-01

---

## Goal

Build a hierarchical concept tree from document content and store it as nodes and edges in the existing `IGraphStore`. Supports both ingestion-time extraction (opt-in) and on-demand extraction via a standalone service.

---

## Architecture

### New types in `Rag.NET.GraphRag`

#### `MindMapNode`

Deserialization target and return type for callers.

```csharp
public sealed record MindMapNode(string Title, string Summary, IReadOnlyList<MindMapNode> Children);
```

#### `MindMapOptions`

```csharp
public sealed class MindMapOptions
{
    /// <summary>Run extraction automatically during ingestion. Default: false.</summary>
    public bool ExtractAtIngestion { get; set; } = false;

    /// <summary>Maximum depth of the generated tree. Default: 3.</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>Optional cheaper model override. Null = use DI-registered IChatClient.</summary>
    public IChatClient? ChatClient { get; set; }

    /// <summary>LLM prompt template. {text} and {depth} are replaced at runtime.</summary>
    public string Prompt { get; set; } = /* see default below */;
}
```

Default prompt asks the LLM to return a JSON tree of `{ title, summary, children[] }` nodes up to `{depth}` levels deep.

#### `MindMapExtractor`

Standalone service. Does not implement any pipeline interface — usable independently.

```csharp
public sealed class MindMapExtractor(IChatClient chatClient, IGraphStore? graphStore, MindMapOptions options)
{
    public async Task<MindMapNode> ExtractAsync(string text, string documentId, CancellationToken ct);
}
```

1. Builds prompt from `MindMapOptions.Prompt`, replacing `{text}` and `{depth}`.
2. Calls `IChatClient` once.
3. Deserializes response into `MindMapNode` tree.
4. If `IGraphStore` is available, writes nodes and edges (see Graph Storage section).
5. Returns root `MindMapNode`.

#### `MindMapExtractionBehavior : IIngestionBehavior`

Thin wrapper around `MindMapExtractor`. Gated by `MindMapOptions.ExtractAtIngestion`.

```csharp
public sealed class MindMapExtractionBehavior(MindMapExtractor extractor, MindMapOptions options)
    : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (options.ExtractAtIngestion)
        {
            var fullText = string.Join("\n\n", ctx.Chunks.Select(c => c.Text));
            var documentId = ctx.Metadata.DocumentId.ToString();
            await extractor.ExtractAsync(fullText, documentId, ct);
        }
        return await next(ctx, ct);
    }
}
```

---

## Graph Storage

Mind-map nodes and edges are stored in the existing `IGraphStore` alongside GraphRAG entities.

| Element | Mapping |
|---|---|
| `MindMapNode` | `GraphEntity` with `Type = "mind_map_node"` |
| Parent → child edge | `GraphRelationship` with `Description = "has_subtopic"`, `Weight = 1.0` |
| Provenance | `SourceDocumentId` set on every entity |

`DeleteByDocumentIdAsync` on `IGraphStore` automatically removes all mind-map nodes for a document — no special cleanup needed.

Callers retrieve the mind-map via `GetFullGraphAsync()` and filter on `Type == "mind_map_node"` and `Description == "has_subtopic"`.

---

## DI Registration

```csharp
// With GraphRAG (shared IGraphStore):
services.AddRagNet(rag => rag
    .UseGraphRag(...)
    .UseMindMapExtraction(o => {
        o.ExtractAtIngestion = true;
        o.MaxDepth = 3;
    }));

// Standalone (no graph persistence):
services.AddRagNet(rag => rag
    .UseMindMapExtraction());
```

`UseMindMapExtraction` resolves `IGraphStore` from DI if registered; otherwise mind-map is returned in-memory only with no persistence.

`MindMapExtractor` is registered as a singleton and available for direct injection.

---

## Error Handling

- If the LLM returns malformed JSON, log a warning and return an empty root node. Do not throw — ingestion pipeline continues.
- If `IGraphStore` write fails, log a warning. The in-memory `MindMapNode` tree is still returned.

---

## Testing

- Unit test `MindMapExtractor` with a stubbed `IChatClient` (NSubstitute).
- Test tree serialization with nested children up to `MaxDepth`.
- Test `MindMapExtractionBehavior` respects `ExtractAtIngestion = false` (no LLM call).
- Test graph storage: verify entity count and relationship count match node/edge count in tree.
- DI test: `UseMindMapExtraction` registers `MindMapExtractor` and `MindMapExtractionBehavior`.
