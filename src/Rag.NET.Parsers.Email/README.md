# Rag.NET.Parsers.Email

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Parsers.Email.svg?logo=nuget&label=Rag.NET.Parsers.Email)](https://www.nuget.org/packages/Rag.NET.Parsers.Email)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Parsers.Email.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Parsers.Email)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
