# Rag.NET.DataProviders.Gmail

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.Gmail.svg?logo=nuget&label=Rag.NET.DataProviders.Gmail)](https://www.nuget.org/packages/Rag.NET.DataProviders.Gmail)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.Gmail.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.Gmail)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Gmail connector for Rag.NET ingestion: reads a mailbox over IMAP with MailKit and OAuth2
(`SaslMechanismOAuth2`), emitting each message as plain text and resuming incrementally
from an IMAP UniqueId watermark.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Gmail
```

## Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Gmail;

var tokenProvider = new OAuthClientCredentialsTokenProvider(
    tokenEndpoint: "https://oauth2.googleapis.com/token",
    clientId:      "my-client-id.apps.googleusercontent.com",
    clientSecret:  Environment.GetEnvironmentVariable("GMAIL_CLIENT_SECRET")!,
    scopes:        ["https://mail.google.com/"]);

services.AddGmailDataProvider(tokenProvider, opts =>
{
    opts.UserName   = "user@example.com";  // the mailbox to authenticate as
    opts.DeltaToken = savedUidWatermark;   // IMAP UniqueId; null = full mailbox
});
```

## Example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;

var provider = sp.GetRequiredService<IFileContentProvider>();
var result = await pipeline.IngestFromProviderAsync(provider, new ProviderId("gmail"), hashStore);
Console.WriteLine($"Ingested {result.Ingested} messages");
```

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
