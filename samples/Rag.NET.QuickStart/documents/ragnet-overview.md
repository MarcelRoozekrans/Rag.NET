# Rag.NET

Rag.NET is a modular retrieval-augmented generation library for .NET. The core `Rag.NET`
package provides `IRagPipeline`, with `IngestAsync` to parse, chunk, embed, and store a
document, and `AskAsync`/`AskStreamingAsync` to retrieve relevant chunks and generate a grounded
answer through any `Microsoft.Extensions.AI` `IChatClient`.

The default chunking strategy is `RecursiveChunkingStrategy`. Additional strategies — semantic
chunking, token-aware chunking, and C# AST-aware chunking — live in separate packages so the
core package stays small.

Vector storage is pluggable: `Rag.NET.VectorStores.PgVector` for PostgreSQL with pgvector,
`Rag.NET.VectorStores.Qdrant` for Qdrant, and several others. An in-memory store ships in the
core package for local development and does not survive a process restart.
