using AirtableApiClient;

namespace Rag.NET.DataProviders.Airtable;

/// <summary>
/// Wraps <see cref="AirtableBase"/> to implement <see cref="IAirtableClient"/>.
/// </summary>
internal sealed class AirtableClientWrapper : IAirtableClient, IDisposable
{
    private readonly AirtableBase _base;

    public AirtableClientWrapper(AirtableBase airtableBase)
    {
        ArgumentNullException.ThrowIfNull(airtableBase);
        _base = airtableBase;
    }

    public Task<AirtableListRecordsResponse> ListRecordsAsync(
        string tableName,
        string? offset = null,
        string? filterByFormula = null,
        string? view = null,
        CancellationToken cancellationToken = default)
    {
        return _base.ListRecords(
            tableName,
            offset,
            fields: null,
            filterByFormula,
            maxRecords: null,
            pageSize: null,
            sort: null,
            view,
            cellFormat: null,
            timeZone: null,
            userLocale: null,
            returnFieldsByFieldId: false,
            includeCommentCount: null,
            cancellationToken);
    }

    public void Dispose() => _base.Dispose();
}
