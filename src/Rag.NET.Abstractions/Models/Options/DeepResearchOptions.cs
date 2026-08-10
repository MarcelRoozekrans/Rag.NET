using ZeroAlloc.Validation;

namespace Rag.NET.Models.Options;

/// <summary>
/// Tuning for Deep Research: iterative retrieval that checks whether accumulated results are
/// sufficient to answer the query and, if not, generates sub-queries and retrieves again, up to
/// <see cref="MaxDepth"/> iterations. Each sufficiency check and sub-query generation is an LLM call.
/// </summary>
[Validate]
public sealed class DeepResearchOptions
{
    /// <summary>
    /// Maximum number of sufficiency-check iterations before returning accumulated results.
    /// Default 3.
    /// <para>
    /// Must be greater than 0 — enforced by the validation attribute, which
    /// <c>RagBuilder.UseDeepResearch</c> runs through the generated
    /// <c>DeepResearchOptionsValidator</c> at registration. A zero or negative depth skips
    /// <c>DeepResearchRetriever</c>'s loop entirely, silently degrading deep research to plain
    /// retrieval while still paying for the decorator.
    /// </para>
    /// </summary>
    [GreaterThan(0)]
    public int MaxDepth { get; set; } = 3;

    /// <summary>
    /// Maximum number of sub-queries generated per insufficiency response. Default 3.
    /// <para>
    /// Must be greater than 0 — enforced by the validation attribute, which
    /// <c>RagBuilder.UseDeepResearch</c> runs through the generated
    /// <c>DeepResearchOptionsValidator</c> at registration. Zero burns one LLM sufficiency call
    /// per iteration while retrieving nothing, so the loop spends <see cref="MaxDepth"/> LLM
    /// calls making no progress; a negative count makes
    /// <c>DeepResearchRetriever.RetrieveAsync</c>'s <c>raw[..subCount]</c> slice throw
    /// mid-retrieval on the first insufficiency response.
    /// </para>
    /// </summary>
    [GreaterThan(0)]
    public int SubQueryCount { get; set; } = 3;

    /// <summary>
    /// Custom sufficiency-check prompt sent to the LLM. When null the built-in default is used.
    /// The custom prompt is sent verbatim — the caller is responsible for embedding the query
    /// and context when using a custom prompt.
    /// </summary>
    public string? SufficiencyPrompt { get; set; }
}
