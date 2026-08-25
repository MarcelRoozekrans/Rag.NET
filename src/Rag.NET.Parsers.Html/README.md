# Rag.NET.Parsers.Html

HTML parser for the Rag.NET ingestion pipeline: AngleSharp-based extraction that strips
scripts, styles and navigation chrome and keeps the readable text — the parser behind
web-page and Confluence/Jira-style HTML ingestion.

## Install

```bash
dotnet add package Rag.NET.Parsers.Html
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the parser registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Html;

rag.AddHtmlParser();
```

### Links

By default a link's URL is appended to its text, so `<a href="/nieuws/laatste">Laatste nieuws</a>`
is indexed as `Laatste nieuws (/nieuws/laatste)`. A site-internal path like that is noise in an
embedding and cannot be followed by anyone reading the chunk later, so there are two alternatives:

```csharp
rag.AddHtmlParser(o => o.HrefHandling = HtmlHrefHandling.Remove);       // Laatste nieuws
rag.AddHtmlParser(o => o.HrefHandling = HtmlHrefHandling.MakeAbsolute); // Laatste nieuws (https://nos.nl/nieuws/laatste)
```

`Remove` drops the **URL**, not the link text — a navigation label is still text worth indexing.

`MakeAbsolute` needs to know where the document came from, and looks in this order: a
`<base href>` element in the page, then the document's `url` tag, then
`HtmlParserOptions.BaseUri`. Every web data provider in this library — crawler, sitemap and RSS —
already records the page URL under `url`, so web-ingested content resolves with nothing configured.
When no base is found the URL is left relative rather than resolved against a guess.

### Sections

The parser emits one section per heading, carrying the text between that heading and the next in
document order, plus a leading section for anything before the first heading. Nesting does not
matter: a heading wrapped in layout containers keeps the content that follows it.

## Example

Registered, the parser claims `text/html` content:

```csharp
using var stream = File.OpenRead("release-notes.html");
var result = await pipeline.IngestAsync(stream, new DocumentMetadata
{
    DocumentId  = new DocumentId("release-notes-2026-07"),
    FileName    = "release-notes.html",
    ContentType = "text/html",
});
```

The parser is also consumed by `Rag.NET.Parsers.Epub` and `Rag.NET.Parsers.Email`, which
delegate their embedded HTML bodies to it.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
