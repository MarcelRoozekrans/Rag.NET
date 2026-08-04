# Rag.NET.DataProviders.Gmail

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
