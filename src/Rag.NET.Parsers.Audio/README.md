# Rag.NET.Parsers.Audio

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
