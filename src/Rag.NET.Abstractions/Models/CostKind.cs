namespace Rag.NET.Models;

/// <summary>
/// The kind of billable call a cost entry was produced by. Not every kind is an LLM call:
/// <see cref="Ocr"/> is a document-processing service billed per page rather than per token.
/// </summary>
public enum CostKind
{
    /// <summary>A chat-completion call (streaming or not).</summary>
    Chat,

    /// <summary>An embedding-generation call.</summary>
    Embedding,

    /// <summary>
    /// A document OCR call. Priced per page, so such an entry carries
    /// <see cref="CostEntry.Pages"/> and zero tokens.
    /// </summary>
    Ocr,
}
