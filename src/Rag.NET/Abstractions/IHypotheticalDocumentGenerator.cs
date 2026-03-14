namespace Rag.NET.Abstractions;

/// <summary>
/// Generates a hypothetical document that would ideally answer a given query.
/// The hypothetical document text is embedded and used for vector similarity search
/// in place of the query, improving recall for asymmetric retrieval (short query vs. long document).
/// </summary>
public interface IHypotheticalDocumentGenerator
{
    /// <summary>
    /// Generates a hypothetical document for <paramref name="query"/>.
    /// The returned text is embedded and used as the search vector.
    /// On failure, callers fall back to embedding the original query directly.
    /// </summary>
    Task<string> GenerateAsync(string query, CancellationToken cancellationToken = default);
}
