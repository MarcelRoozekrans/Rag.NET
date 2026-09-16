# Rag.NET.DataProviders.Slack

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.Slack.svg?logo=nuget&label=Rag.NET.DataProviders.Slack)](https://www.nuget.org/packages/Rag.NET.DataProviders.Slack)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.Slack.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.Slack)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

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
