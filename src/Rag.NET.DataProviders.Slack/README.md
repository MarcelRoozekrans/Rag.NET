# Rag.NET.DataProviders.Slack

Slack connector for Rag.NET ingestion: exports channel message history as plain text with
a bot token, resuming incrementally from a Unix-timestamp watermark (`oldest`).

## Install

```bash
dotnet add package Rag.NET.DataProviders.Slack
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.Slack;

services.AddSlackDataProvider(
    botToken: Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN")!,
    configure: opts =>
    {
        opts.ChannelId  = "C01ABCDEF";     // null = all channels the bot has joined
        opts.DeltaToken = savedDeltaToken; // Unix timestamp; null = full history
    });
```

The bot needs the `channels:history` and `channels:read` scopes and must be a member of
the channels it reads.

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("slack"), hashStore);
Console.WriteLine($"Ingested {result.Ingested} channel exports");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
