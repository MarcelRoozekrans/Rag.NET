using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Notion;

/// <summary>
/// The body of <c>POST /v1/databases/{database_id}/query</c>.
/// <para>
/// Deliberately not <see cref="NotionSearchRequest"/>. The two endpoints differ in ways that
/// would fail quietly if one record served both: search takes a single <c>sort</c> object, the
/// database query takes a <c>sorts</c> <b>array</b>; and search needs its
/// <c>filter: {"value":"page","property":"object"}</c> to avoid returning databases, which is
/// meaningless here because a database query returns only that database's pages. Sending
/// search's shape to this endpoint is accepted and ignored, which is the failure that looks like
/// success — the sort silently would not apply, so delta ingestion would page through the whole
/// database in arbitrary order and stop at the first page older than the token.
/// </para>
/// </summary>
/// <param name="PageSize">How many pages per call; Notion's maximum is 100.</param>
/// <param name="StartCursor">The pagination cursor, or <see langword="null"/> for the first page.</param>
/// <param name="Sorts">
/// Sort directives, applied in order. Delta ingestion sends one descending
/// <c>last_edited_time</c> sort so the newest pages arrive first and paging can stop early.
/// </param>
public sealed record NotionDatabaseQueryRequest(
    [property: JsonPropertyName("page_size")] int PageSize,
    [property: JsonPropertyName("start_cursor")] string? StartCursor,
    [property: JsonPropertyName("sorts")] IReadOnlyList<NotionSort>? Sorts);
