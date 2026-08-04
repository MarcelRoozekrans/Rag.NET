# Rag.NET.Parsers.Email

Email parser for the Rag.NET ingestion pipeline: `.eml` (RFC 822, via MimeKit) and `.msg`
(Outlook, via MsgReader) messages become searchable text — subject, headers and body —
while attachments are dispatched to whichever other parsers you registered.

## Install

```bash
dotnet add package Rag.NET.Parsers.Email
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the parser registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Email;

rag.AddEmailParser();
```

## Example

Attachment dispatch is the point: a PDF attached to an ingested email is parsed by the PDF
parser — register the parsers for the attachment types you expect (for example
`AddPdfParser()` from `Rag.NET.Parsers.Pdf`) next to this one:

```csharp
using Rag.NET.Parsers.Email;

rag.AddEmailParser(options => options.MaxEmbeddedMessages = 50); // default cap
```

The `Rag.NET.DataProviders.Microsoft365` Exchange connector emits raw `.eml` entries and
requires this parser to be registered.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
