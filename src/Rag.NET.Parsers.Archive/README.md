# Rag.NET.Parsers.Archive

ZIP archive parser for the Rag.NET ingestion pipeline: an ingested archive is unpacked
in-memory and every entry is dispatched to the parser registered for its type, with
nesting-depth and entry-count limits guarding against zip bombs.

## Install

```bash
dotnet add package Rag.NET.Parsers.Archive
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the parser registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Archive;

rag.AddArchiveParser();
```

## Example

The limits are configurable; exceeding them throws `ArchiveLimitExceededException`
instead of silently exhausting memory:

```csharp
using Rag.NET.Parsers.Archive;

rag.AddArchiveParser(options =>
{
    options.MaxNestingDepth     = 3;
    options.MaxNestedContainers = 10;
});
```

Entries only become chunks when a parser for their content type is registered — an
archive full of `.docx` files needs `Rag.NET.Parsers.Office` beside this package.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
