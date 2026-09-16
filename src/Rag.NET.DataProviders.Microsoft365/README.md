# Rag.NET.DataProviders.Microsoft365

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.DataProviders.Microsoft365.svg?logo=nuget&label=Rag.NET.DataProviders.Microsoft365)](https://www.nuget.org/packages/Rag.NET.DataProviders.Microsoft365)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.DataProviders.Microsoft365.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.DataProviders.Microsoft365)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

The Microsoft 365 connectors for Rag.NET ingestion in one package: SharePoint document
libraries, OneDrive, Microsoft Teams messages and Exchange/Outlook mail — all via
Microsoft Graph with client-credentials auth.

## Install

```bash
dotnet add package Rag.NET.DataProviders.Microsoft365
```

## Setup

Each source has its own registration; add the ones you ingest:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders.SharePoint;

services.AddSharePointDataProvider(
    tenantId:     "00000000-0000-0000-0000-000000000000",
    clientId:     "my-app-client-id",
    clientSecret: Environment.GetEnvironmentVariable("GRAPH_CLIENT_SECRET")!,
    siteId:       "contoso.sharepoint.com,site-guid,web-guid",
    driveId:      "drive-guid",
    configure: opts =>
    {
        opts.Extensions = [".docx", ".pdf"];
        opts.DeltaToken = savedDeltaLink;  // Graph deltaLink; null = full traversal
    });
```

`AddOneDriveDataProvider` (namespace `Rag.NET.DataProviders.OneDrive`),
`AddMicrosoftTeamsDataProvider` (`...MicrosoftTeams`) and `AddExchangeMailDataProvider`
(`...Exchange`) follow the same tenant/client/secret shape.

## Example

Exchange mail emits raw RFC 822 `.eml` entries so attachments dispatch to your other
parsers — it requires `AddEmailParser()` from `Rag.NET.Parsers.Email`:

```csharp
using Rag.NET.DataProviders.Exchange;

services.AddExchangeMailDataProvider(
    tenantId:     "00000000-0000-0000-0000-000000000000",
    clientId:     "my-app-client-id",
    clientSecret: Environment.GetEnvironmentVariable("GRAPH_CLIENT_SECRET")!,
    configure: opts =>
    {
        opts.Mailbox    = "ingest@contoso.com";  // required mailbox UPN
        opts.DeltaToken = savedWatermark;        // receivedDateTime; null on first run
    });
```

Stale SharePoint/OneDrive delta tokens (`resyncRequired`) fall back to a full traversal
automatically.

## Full guide

- [Data providers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/data-providers.md)
