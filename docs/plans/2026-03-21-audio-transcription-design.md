# Design: Audio Transcription Parser (Whisper.net)

**Date:** 2026-03-21
**Status:** Approved

---

## Overview

New package `Rag.NET.Parsers.Audio` that transcribes audio files into `DocumentSection` records using [Whisper.net](https://github.com/sandrohanea/whisper.net) — a native .NET binding to OpenAI's Whisper model that runs fully local with no API key. One `DocumentSection` per Whisper segment (natural speech boundaries), with timestamps stored in section metadata.

---

## Architecture

### Package Structure

Follows the exact pattern of `Rag.NET.Parsers.Pdf` and `Rag.NET.Parsers.Word`: a separate project referencing `Rag.NET` core and a single external library dependency (`Whisper.net`).

```
src/Rag.NET.Parsers.Audio/
  AudioDocumentParser.cs
  AudioParserOptions.cs
  AudioParserBuilderExtensions.cs
  Rag.NET.Parsers.Audio.csproj
tests/Rag.NET.Parsers.Audio.Tests/
  AudioDocumentParserTests.cs
  Rag.NET.Parsers.Audio.Tests.csproj
```

### `AudioParserOptions`

```csharp
public sealed class AudioParserOptions
{
    public GgmlType ModelType { get; init; } = GgmlType.Base;
    public string? Language { get; init; }              // null = auto-detect
    public string ModelCacheDirectory { get; init; } = Path.GetTempPath();
}
```

`GgmlType` is the Whisper model size enum (`Tiny`, `Base`, `Small`, `Medium`, `Large`). Larger models trade speed and memory for accuracy. `Base` is the default — good accuracy, ~150 MB.

### `AudioDocumentParser`

Implements `IDocumentParser`.

**`CanParse`:** returns `true` for `audio/wav`, `audio/mpeg`, `audio/flac`, `audio/ogg`, `audio/mp4`.

**`ParseAsync` flow:**
1. Copy stream to a temp file (Whisper.net requires a file path)
2. First call: download the GGML model to `ModelCacheDirectory` via `WhisperGgmlDownloader.GetGgmlModelAsync` if not already cached
3. Create `WhisperFactory` from cached model path
4. Build a `WhisperProcessor` with configured language (or auto-detect if null)
5. Process the audio file — receive `SegmentData` objects
6. For each segment with non-whitespace text: yield a `DocumentSection`
7. Delete temp file in `finally` block

**`DocumentSection` shape per segment:**
```csharp
new DocumentSection
{
    Text        = segment.Text.Trim(),
    DocumentId  = metadata.DocumentId,
    SectionIndex = sectionIndex++,
    Metadata    = new Dictionary<string, string>
    {
        ["start_ms"] = ((long)segment.Start.TotalMilliseconds).ToString(),
        ["end_ms"]   = ((long)segment.End.TotalMilliseconds).ToString(),
    }
}
```

`HeadingLevel`, `Heading`, and `PageNumber` are left null — audio has no document structure.

### `AudioParserBuilderExtensions`

```csharp
public static class AudioParserBuilderExtensions
{
    public static RagBuilder AddAudioParser(this RagBuilder builder,
        Action<AudioParserOptions>? configure = null)
    {
        var options = new AudioParserOptions();
        configure?.Invoke(options);  // Note: options is sealed with init-only — use record-style or mutable options
        builder.Services.AddSingleton(options);
        builder.AddParser<AudioDocumentParser>();
        return builder;
    }
}
```

**Usage:**
```csharp
services.AddRagNet(rag =>
{
    rag.AddAudioParser();
    // or
    rag.AddAudioParser(o => { /* configure via new AudioParserOptions */ });
});
```

---

## Error Handling

- **Model download fails:** `IOException` propagates to caller — no partial state, caller can retry.
- **Whisper processing throws:** exception propagates — no partial sections yielded.
- **Stream is empty / too short:** Whisper returns no segments → zero sections yielded, no error.
- **Whitespace-only segment text:** segment is skipped — not yielded.
- **Temp file cleanup:** `finally` block always deletes the temp file, even on exception.
- **`OperationCanceledException`:** re-thrown immediately; temp file still cleaned up via `finally`.

---

## Model Caching

Whisper GGML models are large binary files (74 MB – 2.9 GB depending on size). `WhisperGgmlDownloader` downloads them once to `ModelCacheDirectory`. Subsequent calls check for the file before downloading. The parser checks for the cached file on every `ParseAsync` call (fast path) before triggering a download.

---

## Testing

Whisper cannot run in unit tests (requires a native binary and audio file). Tests mock or stub the segment boundary:

| Scenario | Expected |
|---|---|
| `CanParse("audio/wav")` | `true` |
| `CanParse("audio/mpeg")` | `true` |
| `CanParse("application/pdf")` | `false` |
| `CanParse("text/plain")` | `false` |
| Segments returned → sections yielded | One section per segment; text trimmed; `start_ms`/`end_ms` in metadata |
| Whitespace-only segment | Skipped — no section yielded |
| `Language = "en"` | Passed to Whisper processor builder |
| `Language = null` | Auto-detect — no language call on builder |
| Temp file deleted after successful parse | No temp files in `ModelCacheDirectory` / temp dir |
| Temp file deleted after exception | No leak even when Whisper throws |

Integration test (optional, skipped in CI unless `WHISPER_INTEGRATION=true`): ingest a short `.wav` file with known speech, assert at least one section with non-empty text is returned.

---

## Out of Scope

- Cloud transcription APIs (Azure Speech, OpenAI Whisper API)
- Speaker diarization
- Word-level timestamps
- Streaming transcription
- Video file support (extract audio track — separate concern)
