using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>
/// Subset of Linear's <c>IssueFilter</c> input used by the connector. Unset members are
/// omitted from the JSON — Linear treats an absent filter field as "no constraint",
/// whereas an explicit <c>null</c> would filter for null values.
/// </summary>
public sealed record LinearIssueFilter(
    [property: JsonPropertyName("team"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LinearTeamFilter? Team,
    [property: JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LinearStateFilter? State,
    [property: JsonPropertyName("updatedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LinearDateComparator? UpdatedAt);
