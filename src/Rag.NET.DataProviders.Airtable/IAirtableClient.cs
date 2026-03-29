using AirtableApiClient;

namespace Rag.NET.DataProviders.Airtable;

/// <summary>
/// Thin abstraction over <see cref="AirtableBase"/> for testability.
/// </summary>
internal interface IAirtableClient
{
    /// <summary>Lists records from the specified table, supporting pagination and filtering.</summary>
    Task<AirtableListRecordsResponse> ListRecordsAsync(
        string tableName,
        string? offset = null,
        string? filterByFormula = null,
        string? view = null,
        CancellationToken cancellationToken = default);
}
