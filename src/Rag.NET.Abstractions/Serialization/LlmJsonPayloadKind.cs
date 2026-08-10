namespace Rag.NET;

/// <summary>
/// What the caller expects the JSON payload of an LLM response to be, which governs how far
/// <see cref="LlmJsonExtractor.Extract"/> will go to find it outside a code fence.
/// </summary>
internal enum LlmJsonPayloadKind
{
    /// <summary>A JSON object. Without a fence, the longest balanced <c>{…}</c> that parses wins.</summary>
    Object,

    /// <summary>A JSON array. Without a fence, the longest balanced <c>[…]</c> that parses wins.</summary>
    Array,

    /// <summary>
    /// Only a fence is honoured; a fenceless reply is returned trimmed and otherwise untouched.
    /// For callers that deliberately refuse to salvage bare JSON out of prose — a fence states
    /// the model's intent, a bracket in a sentence does not (see <c>RagasJudge</c>) — and for
    /// payloads with no brackets to scan for, like the bare number
    /// <c>SelfAssessmentConfidenceScorer</c> expects.
    /// </summary>
    FencedOnly,
}
