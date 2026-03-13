# Why RAG?

Large Language Models have strong language understanding but a fixed knowledge cutoff and no access to your private data. Retrieval-Augmented Generation (RAG) bridges this gap by retrieving relevant passages from a document corpus at query time and injecting them into the model's context window before generation. The model then answers using evidence, not just its weights — reducing hallucinations and making answers auditable by pointing to source chunks.

## The problem RAG solves

| Problem | Without RAG | With RAG |
|---------|-------------|----------|
| Private / internal documents | Model has no knowledge | Documents are ingested and searchable |
| Post-cutoff information | Model fabricates or says "I don't know" | Retrieved from indexed corpus |
| Hallucinations | Common for detailed factual questions | Grounded in retrieved passages |
| Auditability | Opaque | Sources returned alongside the answer |

## When to use Rag.NET

Rag.NET is the right fit when:

- You have a corpus of documents (PDFs, Word files, wikis, CSVs, etc.) that should answer user questions.
- You want standard .NET dependency injection patterns and `Microsoft.Extensions.AI` abstractions rather than a Python-centric framework.
- You need to swap vector stores or embedding providers without rewriting application code.
- You want battle-tested retrieval quality improvements — hybrid BM25+semantic search, Lost-in-the-Middle reordering, and redundancy filtering — with a single flag per feature.
- You want end-to-end telemetry (OpenTelemetry, `ILogger`, Polly resilience) wired in from the start.

## When RAG is not the right choice

- **Pure summarisation of a single known document**: just stuff the document into the context window directly.
- **Structured data querying**: if your data is relational and the questions are precise, a Text-to-SQL approach is more reliable.
- **Real-time data**: RAG operates on pre-indexed snapshots. For live data you need an ingestion pipeline that refreshes the index continuously.

## How Rag.NET fits into .NET

Rag.NET is built on `Microsoft.Extensions.AI`, the official .NET AI abstraction layer. It consumes `IEmbeddingGenerator<string, Embedding<float>>` and `IChatClient` — the same interfaces used by the OpenAI, Azure OpenAI, and Ollama .NET packages. This means:

- You register your AI provider once in the DI container.
- Rag.NET picks it up automatically; no adapter code is required.
- Switching providers (e.g., from OpenAI to Ollama for local development) is a single DI registration change.

See [Getting Started](getting-started.md) for the full setup walkthrough.
