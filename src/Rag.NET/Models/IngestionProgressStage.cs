namespace Rag.NET.Models;

/// <summary>Identifies the stage of the ingestion pipeline that just completed.</summary>
public enum IngestionProgressStage
{
    /// <summary>The document has been parsed into sections.</summary>
    Parsing,

    /// <summary>Sections have been split into text chunks.</summary>
    Chunking,

    /// <summary>Embeddings have been generated for all chunks.</summary>
    Embedding,

    /// <summary>Embedded chunks have been written to the vector store.</summary>
    Storing,
}
