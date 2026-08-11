using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Airtable;

/// <summary>Options for the Airtable data provider.</summary>
public sealed class AirtableOptions : CloudStorageOptions
{
    /// <summary>Airtable base ID (e.g. <c>appXXXXXXXXXXXXXX</c>).</summary>
    public required string BaseId { get; set; }

    /// <summary>Name or ID of the table to read records from.</summary>
    public required string TableName { get; set; }

    /// <summary>Optional view name to filter records through.</summary>
    public string? View { get; set; }

    /// <summary>
    /// Name of a "Last modified time" field in the table.
    /// When set together with <see cref="CloudStorageOptions.DeltaToken"/>, enables incremental
    /// ingestion via a <c>LAST_MODIFIED_TIME({Field})&gt;'token'</c> formula filter scoped to
    /// this field. May not contain <c>{</c> or <c>}</c> — Airtable's formula grammar has no
    /// escape for braces inside a field reference, so the provider rejects such names at
    /// construction rather than emitting a broken (or injectable) formula.
    /// Settable (not init-only) so <c>AddAirtableDataProvider</c>'s configure callback can
    /// assign it alongside <see cref="CloudStorageOptions.DeltaToken"/>.
    /// </summary>
    public string? LastModifiedFieldName { get; set; }
}
