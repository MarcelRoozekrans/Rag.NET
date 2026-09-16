# Rag.NET.Chunking.CSharp

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Chunking.CSharp.svg?logo=nuget&label=Rag.NET.Chunking.CSharp)](https://www.nuget.org/packages/Rag.NET.Chunking.CSharp)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Chunking.CSharp.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Chunking.CSharp)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
