namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// One document from a BEIR <c>corpus.jsonl</c> line.
/// </summary>
/// <param name="Id">
/// The <c>_id</c> field — the id BEIR's qrels reference, so this is what must reach
/// <see cref="IrMetrics"/> and never a chunk id.
/// </param>
/// <param name="Title">The <c>title</c> field, empty when the corpus omits it.</param>
/// <param name="Text">The <c>text</c> field: the abstract, for SciFact.</param>
/// <param name="RetrievalText">
/// What actually gets embedded: <see cref="Title"/> and <see cref="Text"/> joined by the loader's
/// separator and trimmed. Stored rather than computed on the fly so the separator is a decision the
/// loader makes once, visibly, instead of a default hidden in a property getter — it shifts the
/// parity number, and <c>BeirLoaderTests</c> pins it.
/// </param>
public sealed record BeirDocument(string Id, string Title, string Text, string RetrievalText);
