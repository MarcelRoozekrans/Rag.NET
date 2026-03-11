# Document Parsers Design

## Goal

Add seven document parsers to Rag.NET covering Office (Word, Excel, PowerPoint), web (HTML, Markdown), and structured data (CSV, JSON) formats.

## Architecture

Seven new NuGet packages, each containing a single `IDocumentParser` implementation following the existing `Rag.NET.Parsers.Pdf` pattern. Each parser implements `CanParse(contentType)` and `ParseAsync(stream, metadata)` yielding `IAsyncEnumerable<DocumentSection>`.

## Package & Content Type Mapping

| Package | Class | Content Types | Dependency |
|---|---|---|---|
| `Rag.NET.Parsers.Word` | `WordDocumentParser` | `application/vnd.openxmlformats-officedocument.wordprocessingml.document` | DocumentFormat.OpenXml |
| `Rag.NET.Parsers.Excel` | `ExcelDocumentParser` | `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` | DocumentFormat.OpenXml |
| `Rag.NET.Parsers.PowerPoint` | `PowerPointDocumentParser` | `application/vnd.openxmlformats-officedocument.presentationml.presentation` | DocumentFormat.OpenXml |
| `Rag.NET.Parsers.Html` | `HtmlDocumentParser` | `text/html` | AngleSharp |
| `Rag.NET.Parsers.Markdown` | `MarkdownDocumentParser` | `text/markdown` | Markdig |
| `Rag.NET.Parsers.Csv` | `CsvDocumentParser` | `text/csv` | (BCL only) |
| `Rag.NET.Parsers.Json` | `JsonDocumentParser` | `application/json` | (BCL only) |

## Parser Behaviors

### Word

One section per heading-delimited block. Populates `Heading`/`HeadingLevel` from Word heading styles (Heading1-Heading6). Skips empty paragraphs.

### Excel

One section per row. Text format: `"Column1: Value1 | Column2: Value2"`. First row treated as headers. Each sheet processed sequentially. `Heading` set to sheet name.

### PowerPoint

One section per slide. Extracts text from all text frames/shapes on the slide. `PageNumber` set to slide number. `Heading` set to slide title shape text if present.

### HTML

Strips tags, extracts visible text. Splits by heading elements (`<h1>`-`<h6>`). Populates `Heading`/`HeadingLevel`. Strips scripts, styles, nav, footer elements. Links converted to `"text (url)"` format.

### Markdown

Uses Markdig to parse AST. Splits by headings (`#`-`######`). Populates `Heading`/`HeadingLevel`. Content between headings becomes a section's text rendered as plain text with markdown syntax stripped.

### CSV

One section per data row. Format: `"Header1: Value1 | Header2: Value2"`. First row = headers. Empty rows skipped.

### JSON

Reads top-level array. One section per array element serialized as indented JSON text. If root is an object (not array), yields single section with the full object.

## DI Registration

Each package provides an extension on `RagBuilder`:

```csharp
builder.AddWordParser();
builder.AddExcelParser();
builder.AddPowerPointParser();
builder.AddHtmlParser();
builder.AddMarkdownParser();
builder.AddCsvParser();
builder.AddJsonParser();
```

Each extension calls `builder.Services.AddSingleton<IDocumentParser, XxxDocumentParser>()`.

## Testing Strategy

- Unit tests for each parser in their own test project
- Embed small test files as embedded resources
- Verify section count, text content, heading extraction, page/slide numbers
- No integration tests needed (pure in-process parsers)
