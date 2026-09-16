---
id: cli
title: Command-Line Tool
sidebar_label: CLI (ragnet)
sidebar_position: 14
---

# Command-Line Tool

`ragnet` is a dotnet global tool that ingests into, and queries, a configured pipeline from the
shell. It writes JSON to stdout, so its output pipes into `jq` and everything downstream of it.

It exists for the loop you run before writing any code: point it at a directory, ask a question,
look at which chunks came back and what they scored. Answering "is my chunking sensible for this
corpus" does not need an application.

## Install

```bash
dotnet tool install --global Rag.NET.Cli
```

## Commands

```
ragnet ingest <path> [--overwrite]   Ingest a file or a directory of files.
ragnet query <question> [--top-k N]  Retrieve chunks relevant to a question.
```

`ragnet` with no arguments, `--help` or `-h` prints usage and exits 0.

```bash
ragnet ingest ./docs
ragnet query "how does the retry policy back off?" --top-k 5
```

`ingest` answers with one entry per file plus a separate error list, so a directory where three
files failed still reports the ones that succeeded:

```json
{
  "Documents": [
    { "FilePath": "./docs/retrieval.md", "DocumentId": "9f2c…", "ChunksStored": 42 }
  ],
  "Errors": [
    { "FilePath": "./docs/broken.pdf", "Message": "…" }
  ]
}
```

`query` answers with the question and the ranked chunks, each carrying its score and metadata:

```json
{
  "Query": "how does the retry policy back off?",
  "Results": [
    { "Text": "…", "Score": 0.82, "Metadata": { "source": "resilience.md" } }
  ]
}
```

Logs go to stderr, never stdout. Stdout is the result channel, and a log line in the middle of it
would break every pipe the tool is meant to feed.

### Re-ingesting the same file makes a second document

`ingest` mints a fresh `DocumentId` per file per run rather than deriving one from the path, so
running it twice over the same directory leaves two copies of everything in the store.

`--overwrite` does not prevent that, and it is worth being plain about why: the flag sets
`IngestionOptions.Overwrite`, which purges *that document's* own prior vectors, BM25 entries and
sidecar record before parsing the new content — and with a new id each run there is never a prior
version of that id to purge. The flag is only meaningful to a caller that supplies a stable
`DocumentId`, which this tool does not.

For a repeatable ingest over a changing corpus, use `Rag.NET.Storage.Sqlite`'s
[content-hash record manager](ingestion.md) from your own code, where you control the id.

### `evaluate` is not implemented

`ragnet evaluate` is recognised and rejected with an explanation rather than silently missing,
because "no such command" would read as a typo. The evaluators in `Rag.NET.Evaluation` score
`EvaluationSample` instances that already carry a predicted answer, and no `IRagEvaluator` is
registered by the configuration binding this tool uses. A working command needs a dataset file
format and an evaluator-selection story, neither of which exists — so it was deferred rather than
half-built. Use [the evaluation guide](evaluation.md) and the library directly meanwhile.

## Configuration

`ragnet` does no pipeline wiring of its own. It binds the `RagNet` configuration section through
`Rag.NET.Hosting` — the same seam `Rag.NET.Mcp.Tool` uses — from `appsettings.json` next to the
working directory, or from environment variables using `__` as the section separator
(`RagNet__ChatClient__ApiKey`).

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
      "Kind": "Qdrant",
      "Qdrant": { "Host": "localhost", "Port": 6334, "CollectionName": "ragnet" }
    }
  }
}
```

One OpenAI-compatible shape covers OpenAI, Azure OpenAI, OpenRouter, Ollama and LM Studio — they
speak the same wire API. A local endpoint (`http://localhost:11434/v1`) may leave `ApiKey` unset;
anything else requires a real key.

`VectorDimensions` must match what the configured embedding model actually produces — 1536 for
`text-embedding-3-small`, 768 for `nomic-embed-text` — and the store has to agree with it.

### The store outlives the process, or it does not

`Kind` selects exactly one of `InMemory`, `Qdrant` and `PgVector`; only the matching block is
read. `InMemory` is the default and needs no setup, but everything ingested is gone when the
process exits — so `ragnet ingest` followed by `ragnet query` are two processes and the second
finds nothing. A warning is logged at startup so this cannot pass unnoticed. Set `Kind` to
`Qdrant` or `PgVector` for anything that has to survive between runs.

The other stores — Weaviate, Pinecone, Chroma, Azure AI Search, Redis — are not reachable from
this configuration shape. Wanting one of those means hosting `Rag.NET` in your own application
rather than driving it from this tool.

## Related tools

`Rag.NET.Mcp.Tool` is the same idea for LLM agents: a dotnet global tool over the same
configuration seam, exposing the pipeline as [MCP tools](mcp.mdx) instead of shell commands.
Configure one and you have configured the other.
