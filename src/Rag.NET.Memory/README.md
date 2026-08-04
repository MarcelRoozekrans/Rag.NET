# Rag.NET.Memory

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
