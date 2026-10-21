# Rag.NET.Parsers.AzureDocumentIntelligence

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Parsers.AzureDocumentIntelligence.svg?logo=nuget&label=Rag.NET.Parsers.AzureDocumentIntelligence)](https://www.nuget.org/packages/Rag.NET.Parsers.AzureDocumentIntelligence)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Parsers.AzureDocumentIntelligence.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Parsers.AzureDocumentIntelligence)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **74 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Azure AI Document Intelligence for Rag.NET: documents are sent to a Document Intelligence
model as a whole, the long-running analyze operation is polled to completion, and the
analyzed pages are recorded to the cost ledger when one is registered.

## Install

```bash
dotnet add package Rag.NET.Parsers.AzureDocumentIntelligence
```

This package does not depend on `Rag.NET.Parsers.Pdf`, so it brings in no PDF library.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
