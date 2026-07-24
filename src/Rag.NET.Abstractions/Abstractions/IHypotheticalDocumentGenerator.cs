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

    /// <summary>
    /// Generates up to <paramref name="count"/> hypothetical documents for <paramref name="query"/>.
    /// Partial failures are tolerated: as long as at least one generation succeeds, the surviving
    /// documents are returned (so the result may contain fewer than <paramref name="count"/> entries).
    /// When every generation fails with an exception, the last exception is rethrown.
    /// Empty responses are dropped; a non-throwing model that returns only empty text yields an empty list.
    /// </summary>
    Task<IReadOnlyList<string>> GenerateManyAsync(string query, int count, CancellationToken cancellationToken = default);
}
