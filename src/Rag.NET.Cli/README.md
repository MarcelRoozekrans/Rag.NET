# Rag.NET.Cli

A command-line tool for Rag.NET, packaged as a .NET global tool (`ragnet`) — ingest documents
into, and retrieve chunks from, a configured RAG pipeline from the shell, no C# project required.

## Install

```bash
dotnet tool install -g Rag.NET.Cli
```

## Configure

`ragnet` wires its pipeline — chat client, embedding generator, vector store — from an
`appsettings.json` next to its working directory, or from environment variables (using `__`
as the section separator, e.g. `RagNet__ChatClient__ApiKey`). A sample
`appsettings.sample.json`, showing every supported `VectorStore:Kind`, ships alongside the
installed binaries; copy it to `appsettings.json` and fill in real values.

```json
{
  "RagNet": {
    "ChatClient": {
      "Endpoint": "https://api.openai.com/v1",
      "ApiKey": "…",
      "Model": "gpt-4o-mini"
    },
    "Embeddings": {
      "Endpoint": "https://api.openai.com/v1",
      "ApiKey": "…",
      "Model": "text-embedding-3-small",
      "VectorDimensions": 1536
    },
    "VectorStore": {
      "Kind": "InMemory | Qdrant | PgVector",
      "Qdrant": { "Host": "…", "Port": 6334, "CollectionName": "…" },
      "PgVector": { "ConnectionString": "…" }
    }
  }
}
```

This is exactly the configuration seam `Rag.NET.Mcp.Tool` uses (`Rag.NET.Hosting`'s
`AddRagNetPipelineFromConfiguration`), so the same rules apply: the chat client and embedding
generator both go through one OpenAI-compatible endpoint (OpenAI, Azure OpenAI, OpenRouter,
Ollama, LM Studio); the vector store is `InMemory` (the default, zero setup, but **every
ingested document is lost when the process exits** — a warning is logged at startup),
`Qdrant`, or `PgVector`. A misconfigured setting fails at startup with a message naming both
the setting and the configuration key that fixes it, before any command runs.

Need a provider outside that set? Host the `Rag.NET.Mcp` or `Rag.NET` library directly in your
own application and register whatever you like; this tool covers the bounded set above and
nothing wider.

## Commands

```bash
# Ingest a single file, or every file under a directory (recursively)
ragnet ingest ./document.md
ragnet ingest ./docs [--overwrite]

# Retrieve the chunks a question matches
ragnet query "What is Retrieval-Augmented Generation?" [--top-k 5]
```

Output goes to stdout as JSON, one object per invocation — meant to be piped to another tool
(`jq`, a script, ...). All diagnostics, warnings, and errors — including the startup validation
above and the `InMemory` warning — go to stderr, never stdout. `ingest` exits `1` if any file
failed (the failures are still listed in the JSON on stdout); `query` exits `1` if retrieval
itself failed.

### `evaluate` — deferred

`ragnet evaluate` prints an explanation to stderr and exits non-zero; it is not implemented.
`Rag.NET.Evaluation`'s evaluators (`EmbeddingDistanceEvaluator`, `LlmJudgeEvaluator`) score
`EvaluationSample` instances that already carry a *predicted* answer — building a working
`evaluate` command means reading a dataset of question/reference pairs in some file format,
running each through the pipeline to produce predictions, and choosing which evaluator to run
them through. None of that is a thin call onto an existing seam the way `ingest`/`query` are:
`AddRagNetPipelineFromConfiguration` registers no `IRagEvaluator`, and no dataset file format
exists anywhere in this repository to parse. Wiring it now would mean inventing that design on
the spot rather than reusing one — a half-working `evaluate` would be worse than an absent one,
so it stays absent until that design exists.

## Full guide

- [MCP server](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/mcp.mdx) — the
  same configuration section, for the `Rag.NET.Mcp.Tool` sibling.
