using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Linear;

/// <summary>Configuration for <see cref="LinearDataProvider"/>.</summary>
public sealed class LinearOptions : CloudStorageOptions
{
    /// <summary>
    /// Valid Linear workflow state <b>types</b> (categories, not display names).
    /// Pinned 2026-07-25 against Linear's public docs
    /// (https://linear.app/docs/configuring-workflows): <c>triage</c> is the optional
    /// inbox category, <c>duplicate</c> is system-managed and intentionally excluded,
    /// and Linear spells it <c>canceled</c> (single "l").
    /// </summary>
    public static readonly IReadOnlyList<string> ValidStateTypes =
        ["triage", "backlog", "unstarted", "started", "completed", "canceled"];

    /// <summary>
    /// Optional team keys (e.g. <c>["ENG", "OPS"]</c>) applied as a
    /// <c>team.key in</c> filter. <see langword="null"/> enumerates all teams.
    /// </summary>
    public IReadOnlyList<string>? TeamKeys { get; set; }

    /// <summary>
    /// Optional workflow state <b>types</b> applied as a <c>state.type in</c> filter.
    /// Values must come from <see cref="ValidStateTypes"/> (validated at registration).
    /// <see langword="null"/> enumerates all states.
    /// </summary>
    public IReadOnlyList<string>? States { get; set; }

    /// <summary>Issues requested per GraphQL page (<c>first</c> variable). Defaults to 50.</summary>
    public int PageSize { get; set; } = 50;
}
