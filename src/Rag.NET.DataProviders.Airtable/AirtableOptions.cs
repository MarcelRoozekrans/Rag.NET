using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Airtable;

/// <summary>Options for the Airtable data provider.</summary>
public sealed class AirtableOptions : CloudStorageOptions
{
    /// <summary>Airtable base ID (e.g. <c>appXXXXXXXXXXXXXX</c>).</summary>
    public required string BaseId { get; init; }

    /// <summary>Name or ID of the table to read records from.</summary>
    public required string TableName { get; init; }

    /// <summary>Optional view name to filter records through.</summary>
    public string? View { get; init; }

    /// <summary>
    /// Name of a "Last modified time" field in the table.
    /// When set together with <see cref="CloudStorageOptions.DeltaToken"/>,
    /// enables incremental ingestion via a <c>LAST_MODIFIED_TIME()</c> formula filter.
    /// </summary>
    public string? LastModifiedFieldName { get; init; }
}
