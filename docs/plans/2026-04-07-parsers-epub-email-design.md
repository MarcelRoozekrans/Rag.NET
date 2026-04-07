# EPUB and Email (EML) Parsers — Design

## Goal

Add two new `IDocumentParser` implementations: one for EPUB e-books and one for EML email files. Both ship as independent packages following the existing parser conventions.

## Architecture

Both parsers implement `IDocumentParser` from `Rag.NET.Abstractions` and register via an `IRagBuilder` extension method. The pattern is identical to every existing parser package (`Rag.NET.Parsers.Html`, `Rag.NET.Parsers.Word`, etc.):

- One `.csproj` referencing `Rag.NET.Abstractions` + the third-party library
- One parser class (`sealed`, `IDocumentParser`)
- One `*BuilderExtensions` static class with `AddXxxParser<TBuilder>`
- Tests in `Rag.NET.Tests`

## Parser 1: EPUB

**Package:** `Rag.NET.Parsers.Epub`
**NuGet dep:** `VersOne.Epub` (MIT)
**Content type:** `application/epub+zip`

### Flow

```
Stream (epub)
  → EpubReader.OpenBookAsync(stream)
  → iterate book.ReadingOrder (spine items, in order)
      → item.Content (HTML string)
      → wrap in MemoryStream
      → HtmlDocumentParser.ParseAsync(chapterStream, metadata, ct)
      → yield each DocumentSection
```

`EpubDocumentParser` takes `HtmlDocumentParser` as a constructor parameter (injected by DI). This avoids duplicating HTML heading logic and means all heading-level `DocumentSection` splitting comes for free.

`SectionIndex` is a running counter across all chapters so the final index is document-global, not per-chapter.

### Registration

```csharp
// EpubDocumentParser depends on HtmlDocumentParser, so register HTML first
services.AddRagNet(rag => rag
    .AddHtmlParser()   // registers HtmlDocumentParser in DI
    .AddEpubParser()); // registers EpubDocumentParser (injects HtmlDocumentParser)
```

`AddEpubParser` registers `HtmlDocumentParser` as a dependency if not already registered. Order in the parser list matters only for `CanParse` dispatch — EPUB and HTML have distinct content types so there is no conflict.

---

## Parser 2: Email (EML)

**Package:** `Rag.NET.Parsers.Email`
**NuGet dep:** `MimeKit` (MIT)
**Content types:** `message/rfc822`

### Flow

```
Stream (.eml)
  → MimeMessage.Load(stream)
  → yield subject as DocumentSection
      { Heading = message.Subject, HeadingLevel = 1, Text = message.Subject }
  → extract body text
      → prefer TextBody (plain text)
      → fall back to HtmlBody → HtmlDocumentParser
  → yield body as DocumentSection(s)
  → for each MimePart attachment where Filename is not null:
      → resolve contentType = attachment.ContentType.MimeType
      → find parser = _parsers.FirstOrDefault(p => p.CanParse(contentType))
      → if parser found:
          → copy attachment to MemoryStream
          → attachmentMetadata = metadata with FileName = attachment.FileName
          → parser.ParseAsync(attachmentStream, attachmentMetadata, ct)
          → yield each section
      → if no parser: skip (log warning)
```

### Constructor injection

```csharp
public sealed class EmailDocumentParser(
    IEnumerable<IDocumentParser> parsers,
    HtmlDocumentParser htmlParser,
    ILogger<EmailDocumentParser> logger) : IDocumentParser
```

`IEnumerable<IDocumentParser>` is the same collection `ParseBehavior` already uses. `HtmlDocumentParser` is injected directly for the HTML body fallback path.

### Out of scope (follow-ups)

- MSG (Outlook proprietary format) — separate `MsgReader` dep, same dispatch pattern
- Nested MIME multipart beyond one level (e.g. a reply chain where an attachment is itself an email)
- Inline images (treated as attachments; will only parse if an image parser is registered)

### Registration

```csharp
services.AddRagNet(rag => rag
    .AddHtmlParser()
    .AddEmailParser());
```

---

## Testing

Both parsers follow the existing test pattern in `Rag.NET.Tests`:

**EPUB tests** (`EpubDocumentParserTests`):
- Parse a minimal valid `.epub` (constructed in-memory with `VersOne.Epub` test helpers or a fixture file) and assert sections are produced per heading
- Empty EPUB (no spine items) → yields nothing
- EPUB with no headings in chapters → yields one section per chapter

**Email tests** (`EmailDocumentParserTests`):
- Plain-text body → one subject section + one body section
- HTML body (no plain text) → subject section + HTML-parsed sections
- Email with a `.txt` attachment → subject + body + attachment sections
- Email with an attachment whose content type has no registered parser → skips attachment gracefully
- Empty body, no attachments → subject section only

No integration tests (no live EPUB or mail server needed — all fixtures are constructed in-memory).

---

## File Layout

```
src/
  Rag.NET.Parsers.Epub/
    Rag.NET.Parsers.Epub.csproj
    EpubDocumentParser.cs
    EpubParserBuilderExtensions.cs
  Rag.NET.Parsers.Email/
    Rag.NET.Parsers.Email.csproj
    EmailDocumentParser.cs
    EmailParserBuilderExtensions.cs

tests/
  Rag.NET.Tests/
    Parsers/
      EpubDocumentParserTests.cs
      EmailDocumentParserTests.cs
```
