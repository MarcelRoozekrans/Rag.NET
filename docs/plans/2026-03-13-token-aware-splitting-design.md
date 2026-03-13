# Token-Aware Splitting Design

**Goal:** Add a `TokenAwareChunkingStrategy` that splits text by token count rather than character count, preventing chunks from exceeding embedding model token limits on dense text (code, URLs, tables).

**Approach:** New class implementing `IChunkingStrategy`. Users opt-in by registering it via DI instead of the default `FixedSizeChunkingStrategy`. `ChunkingOptions.MaxChunkSize` and `Overlap` are reused — they mean **tokens** in this context. Uses `Microsoft.ML.Tokenizers` with `cl100k_base` encoding (OpenAI-compatible) by default.

---

## Section 1: Architecture

`TokenAwareChunkingStrategy` wraps `TiktokenTokenizer.CreateForEncoding(encodingName)`. On each chunk pass: encode the full section text to token IDs, slice by `MaxChunkSize` tokens with `Overlap` token overlap, decode slices back to strings. No word-boundary heuristics needed — the tokenizer handles sub-word boundaries cleanly.

Registered via a new `RagBuilder.UseTokenAwareChunking(string encodingName = "cl100k_base")` fluent method that replaces the default `RecursiveChunkingStrategy` registration.

---

## Section 2: Components

**Modified files:**
- `src/Rag.NET/Rag.NET.csproj` — add `<PackageReference Include="Microsoft.ML.Tokenizers" Version="0.*" />`
- `src/Rag.NET/DependencyInjection/RagBuilder.cs` — add `UseTokenAwareChunking(string encodingName = "cl100k_base")` method

**New files:**
- `src/Rag.NET/Chunking/TokenAwareChunkingStrategy.cs` — implements `IChunkingStrategy`, constructor takes `string encodingName = "cl100k_base"`

**Test files:**
- `tests/Rag.NET.Tests/Chunking/TokenAwareChunkingStrategyTests.cs`

---

## Section 3: Data Flow

**Chunking algorithm:**
1. Receive `DocumentSection.Text`
2. Encode full text: `int[] tokenIds = tokenizer.EncodeToIds(text)`
3. Slide window of `MaxChunkSize` tokens with `Overlap` token step:
   - `start = 0`, advance by `MaxChunkSize - Overlap` each iteration
   - Slice `tokenIds[start..end]`
   - Decode slice back to string: `tokenizer.Decode(slice)`
   - Yield `TextChunk` with decoded text
4. Continue until `start >= tokenIds.Length`

**Registration example:**
```csharp
services.AddRagNet(b => b.UseTokenAwareChunking("cl100k_base"));
```

---

## Section 4: Error Handling

- Empty/null text: yield break (same guard as `FixedSizeChunkingStrategy`)
- Invalid encoding name: `TiktokenTokenizer.CreateForEncoding` throws `ArgumentException` at construction — fails fast at startup, not per-document
- Overlap ≥ MaxChunkSize: guard against infinite loop — throw `ArgumentException` in constructor if `options.Overlap >= options.MaxChunkSize`

---

## Section 5: Testing

Unit tests on `TokenAwareChunkingStrategy` directly:

- Empty text → no chunks
- Text shorter than `MaxChunkSize` tokens → single chunk
- Dense text (long URL, minified code) → all chunks ≤ `MaxChunkSize` tokens (verified by re-encoding and counting)
- Overlap: second chunk starts `MaxChunkSize - Overlap` tokens into first chunk's content
- Encoding round-trip: decoded text is valid UTF-8 and represents the original content
