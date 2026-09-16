# Rag.NET.Chunking.Templates

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Chunking.Templates.svg?logo=nuget&label=Rag.NET.Chunking.Templates)](https://www.nuget.org/packages/Rag.NET.Chunking.Templates)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Chunking.Templates.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Chunking.Templates)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Domain-specific chunking templates for Rag.NET: legal documents, books, academic papers,
Q&A pairs (CSV/XLSX), email threads and resumes — each template knows its domain's
structure (clauses, chapters, sections, rows, turns) and chunks along it.

## Install

```bash
dotnet add package Rag.NET.Chunking.Templates
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the templates register into.

## Setup

Inside your `AddRagNet(...)` builder callback, pick the template that matches the corpus:

```csharp
using Rag.NET.Chunking.Templates;

rag.UseLegalChunking();
```

## Example

Each template has its own options; Q&A pairs, for instance, reads a spreadsheet column
pair and emits one chunk per question/answer:

```csharp
using Rag.NET.Chunking.Templates;

rag.UseQAPairsChunking(options =>
{
    options.QuestionColumn = "Question";
    options.AnswerColumn   = "Answer";
    options.SkipHeader     = true;      // default
});
```

Also available: `UseBookChunking` (chapters and sections), `UseAcademicPaperChunking`
(abstract, sections, references), `UseEmailChunking` (thread turns) and
`UseResumeChunking` (experience, education, skills).

## Full guide

- [Chunking](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/chunking.md)
