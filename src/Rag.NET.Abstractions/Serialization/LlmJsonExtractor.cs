using System.Text.Json;

namespace Rag.NET;

/// <summary>
/// Pulls the JSON payload out of a chat-model response, whatever the model wrapped it in.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because every hand-rolled copy of it was wrong the same way.</b> GraphRAG's
/// entity extraction stripped a code fence only when the response <i>started</i> with one, and a
/// capable model actually opens with "Here is the extracted information in JSON format:" before
/// the fence — so every response failed to parse, the failure was caught into an empty result,
/// and the package extracted zero entities for its entire life while its unit tests, fed only
/// pre-stripped JSON, stayed green. Six other call sites had the same
/// strip-only-when-leading logic or no fence handling at all. One definition, tested against the
/// shapes models actually return, is the fix for the drift as much as for the bug.
/// </para>
/// <para>
/// Order matters: a fenced block is preferred wherever it sits, because prose around a fence may
/// itself contain braces. Only without a usable fence does the bracket scan run — and only for
/// <see cref="LlmJsonPayloadKind.Object"/> and <see cref="LlmJsonPayloadKind.Array"/>.
/// </para>
/// <para>
/// <b>Braces inside string values:</b> the scan is not a naive first-to-last slice. It walks
/// candidates with a string-and-escape-aware balance counter, validates each with
/// <see cref="JsonDocument"/>, and keeps the longest candidate that parses. Braces inside string
/// values never affect the depth count, a brace in surrounding prose produces an unbalanced or
/// invalid candidate that is skipped, and a short decoy (a <c>{}</c> quoted in the preamble)
/// loses to the real payload on length.
/// </para>
/// <para>
/// When nothing matches, the trimmed response is returned as-is so the caller's own
/// deserialization failure path — throw, log, fall back — fires exactly as it did before.
/// </para>
/// </remarks>
internal static class LlmJsonExtractor
{
    private const string Fence = "```";

    /// <summary>Extracts the most plausible JSON payload from <paramref name="text"/>.</summary>
    /// <param name="text">The raw response text.</param>
    /// <param name="kind">What payload the caller expects; see <see cref="LlmJsonPayloadKind"/>.</param>
    internal static string Extract(string text, LlmJsonPayloadKind kind)
    {
        var trimmed = text.Trim();

        var fenced = FirstFencedBlock(trimmed);
        if (fenced is not null && (kind == LlmJsonPayloadKind.FencedOnly || IsValidJson(fenced)))
            return fenced;

        if (kind == LlmJsonPayloadKind.Object)
            return LongestBalancedPayload(trimmed, '{', '}') ?? trimmed;

        if (kind == LlmJsonPayloadKind.Array)
            return LongestBalancedPayload(trimmed, '[', ']') ?? trimmed;

        return trimmed;
    }

    /// <summary>
    /// The body of the first fenced block, wherever it sits: preamble before, prose after, and a
    /// language tag (<c>```json</c>) are all skipped. Null when there is no complete opening
    /// fence line — an unterminated closing fence is tolerated (body runs to end of text), a
    /// fence with no line break after it is not a block.
    /// </summary>
    private static string? FirstFencedBlock(string text)
    {
        var opening = text.IndexOf(Fence, StringComparison.Ordinal);
        if (opening < 0)
            return null;

        var bodyStart = text.IndexOf('\n', opening + Fence.Length);
        if (bodyStart < 0)
            return null;

        var body = text[(bodyStart + 1)..];
        var closing = body.IndexOf(Fence, StringComparison.Ordinal);
        return (closing >= 0 ? body[..closing] : body).Trim();
    }

    /// <summary>
    /// The longest substring that starts at an <paramref name="open"/>, balances to a matching
    /// <paramref name="close"/> outside string literals, and parses as JSON. Null when no
    /// candidate qualifies.
    /// </summary>
    private static string? LongestBalancedPayload(string text, char open, char close)
    {
        string? best = null;
        for (var start = text.IndexOf(open); start >= 0; start = text.IndexOf(open, start + 1))
        {
            if (TryBalance(text, start, open, close) is { } candidate &&
                (best is null || candidate.Length > best.Length) &&
                IsValidJson(candidate))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Walks from <paramref name="start"/> tracking string literals and escapes, so a bracket
    /// inside a JSON string value never counts toward nesting depth. Returns the substring up to
    /// the balancing close, or null when the text ends unbalanced.
    /// </summary>
    private static string? TryBalance(string text, int start, char open, char close)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == open) depth++;
            else if (c == close && --depth == 0) return text[start..(i + 1)];
        }

        return null;
    }

    private static bool IsValidJson(string candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
