# Domain-Specific Chunking Templates — Design

**Date:** 2026-04-01

---

## Goal

A single `Rag.NET.Chunking.Templates` package providing six pre-built, opinionated chunking templates for common document domains. Each template is registered via a single `UseXxx()` call. Templates compose existing strategies where possible (Option B) and implement from scratch only where genuinely new logic is required.

---

## Package

**`Rag.NET.Chunking.Templates`**

Dependencies:
- `MimeKit` — Email parser
- `CsvHelper` — Q&A CSV parsing
- `ClosedXML` — Q&A Excel parsing
- `Rag.NET.Abstractions` — interfaces
- `Rag.NET.Chunking` — `HierarchicalMergerChunkingStrategy` (composed internally)

All six templates are registered via `RagBuilder` extension methods:

```csharp
rag.UseAcademicPaperChunking(o => { o.IncludeReferences = true; })
rag.UseLegalChunking()
rag.UseBookChunking(o => { o.IncludeIndex = true; })
rag.UseQAPairsChunking(o => { o.QuestionColumn = "Q"; o.AnswerColumn = "A"; })
rag.UseEmailChunking(o => { o.IncludeHeaders = true; })
rag.UseResumeChunking()
```

Every chunk produced by any template carries a `"template"` metadata key (e.g. `"academic_paper"`, `"legal"`, `"book"`, `"qa_pairs"`, `"email"`, `"resume"`) for retrieval-time filtering.

---

## Templates

### 1. Academic Papers

**Class:** `AcademicPaperChunkingStrategy : IDocumentChunkingStrategy`

**Logic:**
1. Scan sections in order. Skip front matter — everything before the abstract (title, authors, affiliations, keywords).
2. Emit the abstract as a single chunk tagged `section_type=abstract`.
3. Pass remaining body sections through `HierarchicalMergerChunkingStrategy` for structured chunking. Body chunks tagged `section_type=body`.
4. Filter references/bibliography section by default (`IncludeReferences = false`).

**Options:** `AcademicPaperChunkingOptions`

| Option | Default | Description |
|---|---|---|
| `IncludeReferences` | `false` | Include bibliography/references section. |
| `IncludeAbstract` | `true` | Emit abstract as a standalone chunk. |

**Metadata added:** `template=academic_paper`, `section_type=abstract|body`

**Fallback:** If no abstract heading is detected, chunk entire document normally.

---

### 2. Legal Documents

**Class:** `LegalChunkingStrategy : IDocumentChunkingStrategy`

**Logic:** Thin wrapper around `HierarchicalMergerChunkingStrategy` with pre-configured numbered-clause regex patterns:
- Level 1: `^\d+\.`
- Level 2: `^\d+\.\d+`
- Level 3: `^\d+\.\d+\.\d+`

Adds `clause` metadata from the section heading.

**Options:** `LegalChunkingOptions`

| Option | Default | Description |
|---|---|---|
| `MaxDepth` | `3` | Maximum clause nesting depth to split on. |
| `HeadingPatterns` | *(legal defaults)* | Override clause detection regex patterns. |

**Metadata added:** `template=legal`, `clause=<heading text>`

**Fallback:** If no numbered clauses detected, falls through to standard hierarchical merge.

---

### 3. Books

**Class:** `BookChunkingStrategy : IDocumentChunkingStrategy`

**Logic:** Wraps `HierarchicalMergerChunkingStrategy`. Pre-filters:
- TOC sections: headings matching "Contents", "Table of Contents", or sections where the majority of lines end with a page number pattern (e.g. `\d+$`).
- Index sections: headings matching "Index" (filtered when `IncludeIndex = false`).

Remaining sections passed through hierarchical merge. Adds `chapter` metadata from top-level headings.

**Options:** `BookChunkingOptions`

| Option | Default | Description |
|---|---|---|
| `IncludeIndex` | `false` | Include back-of-book index section. |
| `IncludeForeword` | `true` | Include foreword/preface sections. |

**Metadata added:** `template=book`, `chapter=<top-level heading>`

**Fallback:** If TOC detection finds nothing, chunk normally without filtering.

---

### 4. Q&A Pairs

**Classes:**
- `QAPairsDocumentParser : IDocumentParser` — handles `.csv` and `.xlsx`/`.xls`
- `QAPairsChunkingStrategy : IDocumentChunkingStrategy` — one chunk per row

**Logic:**
- Parser reads each row; produces one `DocumentSection` per row with question text as `Text`.
- Chunking strategy is a pass-through: emits one `TextChunk` per section, storing the answer in `answer` metadata.
- Column names are configurable; auto-detection tries common names (`question`/`answer`, `q`/`a`, `prompt`/`response`).
- If a row is missing the question or answer column value, it is skipped with a logged warning.

**Options:** `QAPairsChunkingOptions`

| Option | Default | Description |
|---|---|---|
| `QuestionColumn` | `null` (auto-detect) | Column name for the question text. |
| `AnswerColumn` | `null` (auto-detect) | Column name for the answer text. |
| `SkipHeader` | `true` | Skip the first row if it is a header. |

**Metadata added:** `template=qa_pairs`, `answer=<answer text>`

**Error handling:** Throws `InvalidOperationException` if file format cannot be parsed or if columns cannot be resolved.

---

### 5. Email

**Classes:**
- `EmailDocumentParser : IDocumentParser` — handles `.eml`
- Uses `HierarchicalMergerChunkingStrategy` for body chunking (registered internally)

**Logic (via MimeKit):**
1. Parse `.eml` file.
2. If `IncludeHeaders = true`, emit a leading chunk with From/To/Subject/Date as structured text, tagged `part=headers`.
3. Extract body text (plain-text part preferred; HTML stripped to plain text as fallback). Emit as `DocumentSection`(s) tagged `part=body`.
4. For each attachment:
   - Text-extractable formats (`.txt`, `.md`, `.csv`): inline as `DocumentSection`(s) with `part=attachment`, `attachment_name=<filename>`.
   - Binary formats (`.pdf`, `.docx`, etc.): emitted as separate `Stream` entries — the normal parser pipeline handles them. Attachment name stored in metadata.
   - Unrecognised formats: skipped with a logged warning.

**Options:** `EmailChunkingOptions`

| Option | Default | Description |
|---|---|---|
| `IncludeHeaders` | `true` | Emit From/To/Subject/Date as a leading chunk. |
| `IncludeAttachments` | `true` | Process attachments. |

**Metadata added:** `template=email`, `part=headers|body|attachment`, `attachment_name=<name>`

**Error handling:** If an attachment cannot be parsed inline, skip it with a warning. Body always processed.

---

### 6. Resumes

**Class:** `ResumeChunkingStrategy : IDocumentChunkingStrategy`

**Logic:**
1. Concatenate all document sections into full text.
2. Make one LLM call (uses DI-registered `IChatClient` unless `ChatClient` option is set) asking for structured JSON extraction of resume sections: `contact_info`, `work_history` (array, one entry per job), `education` (array, one entry per institution), `skills`.
3. Emit one chunk per extracted section. Work history and education produce one chunk per item (company/institution) so each is independently retrievable.

**Options:** `ResumeChunkingOptions`

| Option | Default | Description |
|---|---|---|
| `ChatClient` | `null` | Optional model override. Null uses DI-registered `IChatClient`. |
| `Prompt` | *(built-in)* | Prompt template. `{text}` replaced at runtime. |

**Metadata added:** `template=resume`, `section=contact_info|work_history|education|skills`

**Error handling:** If LLM returns malformed JSON, log a warning and fall back to chunking full text as a single chunk. Never throws.

---

## DI Registration

```csharp
// Minimal — use all defaults
services.AddRagNet(rag => rag.UseLegalChunking());

// With options
services.AddRagNet(rag => rag.UseQAPairsChunking(o =>
{
    o.QuestionColumn = "prompt";
    o.AnswerColumn = "response";
}));

// Resume with cheaper model override
services.AddRagNet(rag => rag.UseResumeChunking(o =>
{
    o.ChatClient = new OllamaChatClient("llama3");
}));
```

Each `UseXxx()` registers:
- The strategy as `IDocumentChunkingStrategy` (and `IDocumentParser` where applicable)
- The options class as a singleton
- For Email and Q&A Pairs: the parser registered alongside the main parsers so content-type dispatch selects it automatically

---

## Testing

**Test project:** `tests/Rag.NET.Chunking.Templates.Tests`

**Per template:**
- Embedded fixture files: `.eml` sample, `.csv` sample, legal text, academic paper text, book excerpt, resume text
- Verify chunk count, metadata keys/values
- Verify filtered content is absent (no TOC chunks in book output, no front matter in academic output)
- Resume test stubs `IChatClient` via NSubstitute

**DI tests** (in `tests/Rag.NET.Tests/DependencyInjection/`):
- One test per template: `UseXxxChunking()` registers `IDocumentChunkingStrategy` (and `IDocumentParser` where applicable)
