# Rag.NET.Memory

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Memory.svg?logo=nuget&label=Rag.NET.Memory)](https://www.nuget.org/packages/Rag.NET.Memory)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Memory.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Memory)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Persistent conversation memory for Rag.NET: past exchanges are stored in SQLite and the
relevant ones are recalled into context by semantic similarity — so long-running
assistants remember beyond the trimmed in-memory history window.

## Install

```bash
dotnet add package Rag.NET.Memory
```

## Setup

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Memory;
using Rag.NET.Models.Options;

services.AddRagNet(rag => rag
    .UseConversationMemory(
        options: new ConversationMemoryOptions { MaxExchanges = 20 },
        configure: mem => mem.UsePersistentMemory()));
```

## Example

Tune how many stored exchanges are recalled and how similar they must be:

```csharp
using Rag.NET.Memory;
using Rag.NET.Models.Options;

mem.UsePersistentMemory(new PersistentMemoryOptions
{
    TopK     = 5,     // recalled exchanges per question
    MinScore = 0.75,  // below this similarity, stay silent
});
```

At call time, pass the running history via `RagOptions.ConversationHistory`; recalled
exchanges are merged in before the LLM call.

## Full guide

- [Conversation memory](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/memory.md)
