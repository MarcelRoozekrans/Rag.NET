# Rag.NET.Parsers.Audio

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Parsers.Audio.svg?logo=nuget&label=Rag.NET.Parsers.Audio)](https://www.nuget.org/packages/Rag.NET.Parsers.Audio)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Parsers.Audio.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Parsers.Audio)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Audio transcription parser for the Rag.NET ingestion pipeline: speech in ingested audio
files is transcribed locally with Whisper.net — no cloud transcription service involved.

## Install

```bash
dotnet add package Rag.NET.Parsers.Audio
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the parser registers into.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Audio;

rag.AddAudioParser();
```

## Linux needs `libgomp1`

Whisper's native library links OpenMP, and slim Linux images do not carry it. Debian-based
.NET images (`mcr.microsoft.com/dotnet/*`) are among them, so this is the common case rather
than an exotic one:

```dockerfile
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1
```

Without it the first transcription throws `Failed to load native whisper library. Error:
Cannot load the library on this platform using NativeLibrary. PInvokeError: No such file or
directory` — which names neither OpenMP nor the missing file, so it is worth recognising.
Measured on `mcr.microsoft.com/dotnet/sdk:10.0` (linux-x64): the real-transcription tests fail
before installing `libgomp1` and pass after, with no other change. Windows and macOS need
nothing extra.

## Example

The Whisper model is selected (and downloaded to a local cache on first use) through the
options:

```csharp
using Rag.NET.Parsers.Audio;
using Whisper.net.Ggml;

rag.AddAudioParser(new AudioParserOptions
{
    ModelType = GgmlType.Base,   // larger models transcribe better, slower
    Language  = "en",            // null = auto-detect
});
```

Transcripts enter the pipeline as plain text and are chunked and embedded like any other
document.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
