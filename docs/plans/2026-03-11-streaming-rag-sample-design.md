# Streaming RAG + Sample App Design

## Goal

Add streaming response support to the RAG pipeline and create a working end-to-end console sample app.

## Part 1: Streaming API

Add `AskStreamingAsync` to `IRagPipeline` as a first-class pipeline operation:

```csharp
IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
    string query,
    RagOptions? options = null,
    CancellationToken cancellationToken = default);
```

New model:

```csharp
public sealed record RagStreamingUpdate
{
    public string? TextDelta { get; init; }
    public IReadOnlyList<SearchResult>? Sources { get; init; }
}
```

**Flow:**

1. Call `RetrieveAsync` to get sources
2. Yield first update with `Sources` populated (no text yet)
3. Call `IChatClient.GetStreamingResponseAsync` with the same prompt construction as `AskAsync`
4. Yield each token as `RagStreamingUpdate` with `TextDelta`

Sources arrive before any tokens, so consumers can display them while text streams in.

## Part 2: Sample Console App

Update `samples/Rag.NET.Sample` to be a working end-to-end demo.

### What it does

1. Starts a PostgreSQL container via Testcontainers (Docker required)
2. Ingests all files from a `documents/` subfolder (ships with 2-3 small .txt/.md sample files)
3. Enters an interactive Q&A loop with streaming responses
4. Type `quit` to exit

### Provider configuration

- **Default:** Ollama (`http://localhost:11434`, model `llama3.2`, embeddings `all-minilm`)
- **OpenAI:** Set `RAG_PROVIDER=openai` + `OPENAI_API_KEY` env vars
- Uses `Microsoft.Extensions.AI.Ollama` and `Microsoft.Extensions.AI.OpenAI` packages

### Vector store

PgVector with Testcontainers-managed PostgreSQL. Docker is a prerequisite.

### DI wiring

```csharp
services.AddRagNet(rag => rag
    .UsePgVector(connectionString, vectorDimensions: 384)
    .AddPdfParser()
    .AddMarkdownParser());
```

### Interactive loop

```csharp
await foreach (var update in pipeline.AskStreamingAsync(input))
{
    if (update.Sources is not null)
        Console.WriteLine($"Found {update.Sources.Count} sources");
    if (update.TextDelta is not null)
        Console.Write(update.TextDelta);
}
```

## Testing Strategy

- **Streaming:** Unit test `RagPipeline.AskStreamingAsync` with a mocked `IChatClient` returning canned streaming updates. Verify sources come first, then text deltas.
- **Sample app:** No automated tests. Include README with setup instructions (Docker, Ollama model pull).
