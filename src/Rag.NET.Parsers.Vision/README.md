# Rag.NET.Parsers.Vision

[![NuGet](https://img.shields.io/nuget/v/Rag.NET.Parsers.Vision.svg?logo=nuget&label=Rag.NET.Parsers.Vision)](https://www.nuget.org/packages/Rag.NET.Parsers.Vision)
[![Downloads](https://img.shields.io/nuget/dt/Rag.NET.Parsers.Vision.svg?logo=nuget&label=downloads)](https://www.nuget.org/packages/Rag.NET.Parsers.Vision)
[![Docs](https://img.shields.io/badge/docs-website-blue)](https://marcelroozekrans.github.io/Rag.NET/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Rag.NET)](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/LICENSE)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

> Part of **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** — a modular RAG pipeline for .NET built on
> Microsoft.Extensions.AI. Ships as **73 packages**; install only what you call.
> **[Documentation](https://marcelroozekrans.github.io/Rag.NET/)** · **[Choosing packages](https://marcelroozekrans.github.io/Rag.NET/guide/choosing-packages)**

Image and video parsers for the Rag.NET ingestion pipeline: a vision-capable `IChatClient`
describes each image (optionally after a local OCR attempt), and videos are split into
scene keyframes with FFMpeg before description.

## Install

```bash
dotnet add package Rag.NET.Parsers.Vision
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the parsers register into. Video parsing additionally needs
an `ffmpeg` binary on the PATH.

## Setup

Inside your `AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Vision;

rag.UseImageDescription()
   .UseVideoDescription();
```

## Example

Both parsers default to the pipeline's registered chat client; the options let you route
description to a dedicated (cheaper or vision-specialised) model and tune scene detection:

```csharp
using Rag.NET.Parsers.Vision;

rag.UseImageDescription(options =>
{
    options.TryOcrBeforeVision = true;  // screenshots: try OCR first, LLM second
    options.OcrMinCharacters   = 50;    // OCR result shorter than this falls through
    options.OcrLanguage        = "deu";  // Tesseract language code; "deu+eng" for several
    options.TessDataPath       = "./tessdata";  // where the .traineddata files live
});

rag.UseVideoDescription(options =>
{
    options.SceneChangeThreshold = 0.3; // lower = more keyframes
    options.MaxScenes            = 50;
});
```

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
