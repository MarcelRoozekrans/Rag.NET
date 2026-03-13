# Header-Aware Metadata Propagation Design

**Goal:** Propagate heading hierarchy from `DocumentSection` into `TextChunk.Metadata` during ingestion.

**Architecture:** The Markdown and HTML parsers already populate `DocumentSection.Heading` and `DocumentSection.HeadingLevel`. `RagPipeline.ParseAndChunkAsync` iterates sections — the right place to track a 6-slot breadcrumb array and merge heading context into each chunk's metadata.

**Metadata keys added to `TextChunk.Metadata`:**
- `heading` — the section's own heading text (e.g., "Subsection 3")
- `heading_level` — numeric level as string (e.g., "2")
- `heading_breadcrumb` — full hierarchy path (e.g., "Chapter 1 > Section 2 > Subsection 3")

**Breadcrumb logic:** Track `string?[6]` across sections. When a section with `HeadingLevel = N` arrives, set slot `N-1` to the heading text and clear slots `N..5`. Breadcrumb = slots `0..N-1` joined with " > ".

**No new types.** `TextChunk.Metadata` is already `IDictionary<string, string>`.

**Sections without headings** — no metadata keys added (parsers that don't emit heading info are unaffected).
