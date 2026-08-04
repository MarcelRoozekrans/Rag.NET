# Rag.NET.Chunking.Templates

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
