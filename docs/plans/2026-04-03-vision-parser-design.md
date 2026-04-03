# Vision Parser Design

**Date:** 2026-04-03
**Package:** `Rag.NET.Parsers.Vision`
**Feature backlog entry:** Image / Video Description via Vision LLM

---

## Goal

Add image and video document parsers that use a vision LLM to generate natural-language descriptions of visual content, storing each description as a `DocumentSection` for downstream chunking and retrieval.

## Architecture

Single package `Rag.NET.Parsers.Vision` containing two independent parsers and one internal shared helper:

```
Rag.NET.Parsers.Vision/
  ImageDocumentParser          — IDocumentParser
  VideoDocumentParser          — IDocumentParser
  ImageDescriptionOptions
  VideoDescriptionOptions
  RagBuilderExtensions         — UseImageDescription() / UseVideoDescription()
  internal PromptInjectionSanitiser
```

Both parsers:
- Accept `IChatClient` via constructor (DI) with an optional `options.ChatClient` override for a cheaper model
- Yield `DocumentSection` (not `TextChunk`) — feeds the normal chunking pipeline downstream
- Run all LLM output through `PromptInjectionSanitiser` before yielding
- Follow the same structural pattern as `EmailDocumentParser` and `ResumeChunkingStrategy`

## ImageDocumentParser

### Supported formats
PNG, JPG, JPEG, GIF, WEBP, BMP — matched by file extension and `image/*` content type.

### Flow
1. Read image bytes from stream
2. If `TryOcrBeforeVision = true`: run Tesseract OCR; if result length ≥ `OcrMinCharacters`, yield OCR text directly (skip LLM call)
3. Base64-encode image bytes; send to vision LLM with configured prompt
4. Sanitise LLM output via `PromptInjectionSanitiser`
5. Yield one `DocumentSection`:
   - `Text` = sanitised description
   - `Heading = "image_description"`
   - `Metadata`: `source_type=image`, `file_name=<name>`

### Options

| Option | Default | Description |
|---|---|---|
| `ChatClient` | `null` | Optional model override. Null uses DI `IChatClient`. |
| `Prompt` | `"Describe this image in detail, focusing on any text, data, charts, or diagrams."` | LLM prompt. `{fileName}` replaced at runtime. |
| `TryOcrBeforeVision` | `false` | Run Tesseract OCR first; skip vision LLM call if OCR yields sufficient text. |
| `OcrMinCharacters` | `50` | Minimum OCR character count to accept OCR result and skip vision LLM. |
| `SanitiseOutput` | `true` | Strip prompt injection patterns from LLM description before storing. |

### DI Registration
```csharp
services.AddRagNet(rag => rag.UseImageDescription(o =>
{
    o.ChatClient = cheaperVisionClient; // optional
    o.TryOcrBeforeVision = true;
}));
```

## VideoDocumentParser

### Supported formats
MP4, MOV, MKV, AVI, WEBM — matched by file extension.

### Flow
1. Write stream to a temp file (`Path.GetTempFileName()`) — FFmpeg requires a seekable file path
2. Use `FFMpegCore` to detect scene changes via `select='gt(scene,{threshold})'` filter — yields a list of scene-boundary timestamps
3. Cap scene count at `MaxScenes`; select evenly-spaced subset if over the cap
4. Extract one JPEG frame per scene boundary in-memory via FFMpeg
5. For each frame: send to vision LLM (same path as `ImageDocumentParser`), sanitise output
6. Yield one `DocumentSection` per scene:
   - `Text` = sanitised description
   - `Heading = "video_scene_{index}"`
   - `Metadata`: `source_type=video`, `file_name=<name>`, `timestamp_seconds=<t>`
7. Delete temp file in `finally` block

### Options

| Option | Default | Description |
|---|---|---|
| `ChatClient` | `null` | Optional model override. Null uses DI `IChatClient`. |
| `Prompt` | `"Describe this video frame in detail, noting any visible text, actions, or context."` | LLM prompt. `{fileName}`, `{timestamp}` replaced at runtime. |
| `SceneChangeThreshold` | `0.3` | FFmpeg scene detection sensitivity (0.0–1.0). Lower = more scenes detected. |
| `MaxScenes` | `50` | Cap on scenes extracted per video — prevents runaway LLM costs on long videos. |
| `SanitiseOutput` | `true` | Strip prompt injection patterns from LLM descriptions before storing. |

### DI Registration
```csharp
services.AddRagNet(rag => rag.UseVideoDescription(o =>
{
    o.SceneChangeThreshold = 0.4;
    o.MaxScenes = 20;
}));
```

### External dependency
Requires FFmpeg binaries available on the host PATH (or configured via `FFOptions`).

## PromptInjectionSanitiser (internal)

Regex-based guard applied to all vision LLM output before storage. Not publicly exposed — the full `IChunkSanitiser` abstraction is tracked separately in the Prompt Injection Fortification backlog item.

**Patterns targeted:**
- Role-switch phrases: `"ignore previous instructions"`, `"you are now"`, `"act as"`, `"disregard"`, `"new instructions"`, `"system prompt"` (case-insensitive)
- Delimiter injection: `<|system|>`, `<|user|>`, `[INST]`, `###` instruction blocks
- Null-byte and excessive whitespace padding (common obfuscation)

**Behaviour on match:**
- Replace matched span with `[REDACTED]`
- Emit `LogLevel.Warning` via `[LoggerMessage]` including `fileName` and matched pattern
- Still yield the section — silent drops mask attacks; `[REDACTED]` + log makes them auditable

## Package Dependencies

```xml
<PackageReference Include="FFMpegCore" Version="5.*" />
<PackageReference Include="Tesseract" Version="5.*" Condition="'$(EnableOcr)'=='true'" />
<PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.*" />
```

`Tesseract` is an opt-in concern — callers only need the package if `TryOcrBeforeVision = true`. The code compiles without it; a runtime check throws a clear `InvalidOperationException` if Tesseract is unavailable when the option is enabled.

## Testing Strategy

- `ImageDocumentParser`: unit tests with a small real PNG (embedded test resource); assert section heading, metadata keys, `[REDACTED]` behaviour when injection pattern present; assert OCR fast-path taken when `TryOcrBeforeVision = true` and OCR returns sufficient text (mock Tesseract)
- `VideoDocumentParser`: integration test with a short real MP4 (embedded test resource); assert one section per detected scene, metadata `timestamp_seconds` present; assert `MaxScenes` cap respected
- `PromptInjectionSanitiser`: table-driven unit tests for each pattern category; assert `[REDACTED]` substitution and warning logged

## Security Notes

This parser is expected to process potentially untrusted content (email attachments, crawled pages, user uploads). The `PromptInjectionSanitiser` is the first line of defence. Operators handling untrusted sources should also:
- Set `trust_level=external` metadata at ingestion (supported via `DocumentMetadata`)
- Apply prompt hardening in their answer-engine system prompt
- Refer to the Prompt Injection Fortification backlog item for the full mitigation stack
