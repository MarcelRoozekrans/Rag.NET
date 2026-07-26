namespace Rag.NET.Parsers.Email;

/// <summary>
/// Bounds on how far <see cref="EmailDocumentParser"/> and <see cref="MsgDocumentParser"/>
/// follow messages embedded inside messages.
/// </summary>
/// <remarks>
/// Both limits are safety bounds, not preferences: an <c>.eml</c> containing a <c>.msg</c>
/// containing an <c>.eml</c> alternates between two parser instances, so a crafted file can
/// drive an arbitrarily deep chain of parses. Exceeding either limit logs a warning and skips
/// the offending branch — the parsers degrade rather than throw.
/// </remarks>
public sealed class EmailParserOptions
{
    /// <summary>
    /// Hard ceiling on <see cref="MaxEmbeddedDepth"/>, enforced by <c>AddEmailParser</c>.
    /// </summary>
    /// <remarks>
    /// Recursion into an embedded message is <b>stack-recursive</b>: each level adds frames
    /// that are not unwound until the nested enumeration finishes. Measured on this parser,
    /// 480 levels survive and 500+ terminate the process with <c>0xC00000FD</c>
    /// (<c>STATUS_STACK_OVERFLOW</c>) — uncatchable, so no amount of degraded-never-broken
    /// handling helps. About 40 KB of hand-crafted MIME reaches 500 levels, which makes the
    /// crash cheap to trigger once the depth bound allows it. 64 is 21× the default and leaves
    /// an order of magnitude of headroom below the measured floor.
    /// </remarks>
    public const int MaxSupportedEmbeddedDepth = 64;

    /// <summary>
    /// Maximum nesting level followed below the top-level message. <c>0</c> disables
    /// recursion entirely (embedded messages are skipped without a warning, since the skip is
    /// then deliberate); the default of <c>3</c> covers a forward of a forward of a forward.
    /// Must not exceed <see cref="MaxSupportedEmbeddedDepth"/> — see that field for why the
    /// ceiling is not a preference.
    /// </summary>
    public int MaxEmbeddedDepth { get; set; } = 3;

    /// <summary>
    /// Total number of embedded messages parsed per top-level document, across every branch
    /// and every nesting level. Caps the fan-out that <see cref="MaxEmbeddedDepth"/> alone
    /// leaves open (a message with a thousand embedded messages is only one level deep).
    /// </summary>
    public int MaxEmbeddedMessages { get; set; } = 50;
}
