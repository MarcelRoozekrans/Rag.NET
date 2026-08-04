# Rag.NET.Parsers.Vision

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
});

rag.UseVideoDescription(options =>
{
    options.SceneChangeThreshold = 0.3; // lower = more keyframes
    options.MaxScenes            = 50;
});
```

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)
