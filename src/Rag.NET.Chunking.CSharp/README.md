# Rag.NET.Chunking.CSharp

Roslyn-based chunking for C# source in Rag.NET: chunks follow the syntax tree — types,
members, and their doc comments — instead of cutting through the middle of a method the
way size-based strategies do.

## Install

```bash
dotnet add package Rag.NET.Chunking.CSharp
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the strategy registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Chunking.CSharp;

rag.UseCSharpChunking();
```

## Example

The options decide how much of the source becomes retrievable:

```csharp
using Rag.NET.Chunking.CSharp;

rag.UseCSharpChunking(options =>
{
    options.IncludePrivateMembers  = false; // default: public surface only
    options.IncludeInternalMembers = true;  // default
    options.IncludeBodies          = true;  // default: keep implementations, not just signatures
});
```

For repositories mixing C# with other languages, the language-agnostic `UseCodeChunking`
from `Rag.NET.Chunking` handles the rest by file extension.

## Full guide

- [Chunking](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/chunking.md)
