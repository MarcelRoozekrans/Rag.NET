using Microsoft.ML.Tokenizers;
using System.Runtime.InteropServices;

namespace Rag.NET.Chunking;

/// <summary>
/// Canonical token-window slicing over the ORIGINAL text using token char offsets — never by
/// decoding token windows: byte-level BPE (cl100k) can split a multi-byte character across
/// tokens, and decoding would emit U+FFFD replacement chars (corrupting text and drifting
/// spans).
/// Two modes:
/// <list type="bullet">
/// <item><description><c>overlapTokens == 0</c> (partition mode): windows are contiguous,
/// non-decreasing spans; the final window is forced to the text end, so the spans exactly
/// partition the text (used by proposition passages).</description></item>
/// <item><description><c>overlapTokens &gt; 0</c> (overlap mode): each window carries its own
/// span <c>[firstTokenStart .. lastTokenEnd]</c>; adjacent spans intentionally overlap and do
/// NOT partition the text (used by late chunking).</description></item>
/// </list>
/// In both modes windows that advance zero chars past the covered end (e.g. a window falling
/// entirely inside one multi-byte character) are skipped.
/// </summary>
internal static class TokenWindowSplitter
{
    private static readonly Tokenizer Cl100kTokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    /// <summary>A token window: inclusive token index range plus its char span in the text.</summary>
    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct TokenWindow(int FirstToken, int LastToken, int Start, int End);

    /// <summary>Tokenizes <paramref name="text"/> with cl100k_base and windows the offsets.</summary>
    public static List<TokenWindow> SplitByCl100kTokens(string text, int windowTokens, int overlapTokens)
    {
        var tokens = Cl100kTokenizer.EncodeToTokens(text, out _);
        var offsets = new (int Start, int End)[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
            offsets[i] = (tokens[i].Offset.Start.Value, tokens[i].Offset.End.Value);

        return Split(offsets, text.Length, windowTokens, overlapTokens);
    }

    /// <summary>
    /// Windows <paramref name="tokenOffsets"/> with step <c>windowTokens - overlapTokens</c>.
    /// The last window may be shorter; iteration stops once a window reaches the final token.
    /// Callers must guarantee <c>windowTokens &gt; 0</c> and
    /// <c>0 &lt;= overlapTokens &lt; windowTokens</c>.
    /// </summary>
    public static List<TokenWindow> Split(
        IReadOnlyList<(int Start, int End)> tokenOffsets,
        int textLength,
        int windowTokens,
        int overlapTokens)
    {
        var windows = new List<TokenWindow>();
        var step = windowTokens - overlapTokens;
        var coveredEnd = 0;
        for (var first = 0; first < tokenOffsets.Count; first += step)
        {
            var last = Math.Min(first + windowTokens, tokenOffsets.Count) - 1;
            var isFinal = last == tokenOffsets.Count - 1;

            int start, end;
            if (overlapTokens == 0)
            {
                // Partition mode: contiguous spans, clamped non-decreasing; the final window
                // always closes the partition at the text end.
                start = coveredEnd;
                end = isFinal ? textLength : Math.Max(tokenOffsets[last].End, coveredEnd);
            }
            else
            {
                // Overlap mode: each window's own token span; spans intentionally overlap.
                start = tokenOffsets[first].Start;
                end = Math.Max(tokenOffsets[last].End, start);
            }

            // Skip windows that advance zero chars (e.g. entirely inside a multi-byte char).
            if (end > coveredEnd)
            {
                windows.Add(new TokenWindow(first, last, start, end));
                coveredEnd = end;
            }

            if (isFinal)
                break;
        }

        return windows;
    }
}
