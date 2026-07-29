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
    /// Hard ceiling on <see cref="MaxEmbeddedDepth"/>. Absolute: the
    /// <see cref="MaxEmbeddedDepth"/> setter clamps to it, so no construction path — direct
    /// <c>new EmailDocumentParser(…, options)</c>, or mutating the options instance resolved
    /// from DI after <c>AddEmailParser</c> validated it — can exceed it. <c>AddEmailParser</c>
    /// additionally <b>throws</b> on an out-of-range value so misconfiguration is loud at
    /// startup rather than silently clamped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recursion into an embedded message is <b>stack-recursive</b>: each level adds frames
    /// that are not unwound until the nested enumeration finishes. Measured on this parser,
    /// 480 levels survive and 500+ terminate the process with <c>0xC00000FD</c>
    /// (<c>STATUS_STACK_OVERFLOW</c>) — uncatchable, so no amount of degraded-never-broken
    /// handling helps. About 40 KB of hand-crafted MIME reaches 500 levels, which makes the
    /// crash cheap to trigger once the depth bound allows it. 64 is 21× the default and leaves
    /// an order of magnitude of headroom below the measured floor.
    /// </para>
    /// <para>
    /// <b>The ceiling cannot be engineered away, and is not a placeholder for a fix nobody has
    /// got to.</b> The obvious candidate — rewriting the traversal as an explicit work queue so
    /// depth costs heap rather than stack — cannot reach the frames that matter.
    /// <see cref="EmailAttachmentDispatcher"/> selects a parser by <b>content type</b> and
    /// re-enters through the public <see cref="Abstractions.IDocumentParser"/> boundary, so a
    /// chain of embedded messages runs <c>ParseAsync → DispatchAsync → ParseAsync → …</c> where
    /// each hop may land in an arbitrary third-party parser. A queue of ours can only unwind
    /// frames we own. That indirection is deliberate: it replaced a
    /// <c>ReferenceEquals(parser, self)</c> check that missed <c>.eml → .msg → .eml</c> chains
    /// entirely, because consecutive levels there are handled by <i>different</i> parser
    /// instances. Flattening only the case where the resolved parser is one of ours would keep
    /// the ceiling for every other case, at the cost of a second traversal path and the risk of
    /// changing section ordering.
    /// </para>
    /// <para>
    /// Three numbers make 64 the right value: real forwarded-mail chains run <b>10–20</b> levels
    /// deep, the ceiling is <b>64</b>, and the measured overflow floor is <b>~500</b>. It sits
    /// several times above any real input and an order of magnitude below the failure point.
    /// What this bound was missing was the reasoning, not a replacement.
    /// </para>
    /// </remarks>
    public const int MaxSupportedEmbeddedDepth = 64;

    private const int DefaultEmbeddedDepth = 3;

    private int _maxEmbeddedDepth = DefaultEmbeddedDepth;

    /// <summary>
    /// Maximum nesting level followed below the top-level message. <c>0</c> disables
    /// recursion entirely (embedded messages are skipped without a warning, since the skip is
    /// then deliberate); the default of <c>3</c> covers a forward of a forward of a forward.
    /// </summary>
    /// <remarks>
    /// The setter <b>clamps</b> to <see cref="MaxSupportedEmbeddedDepth"/> — see that field for
    /// why the ceiling is not a preference. Clamping rather than validating here is what makes
    /// the ceiling absolute: both parsers have public constructors taking an
    /// <see cref="EmailParserOptions"/>, and the instance <c>AddEmailParser</c> registers in DI
    /// is the same one both parsers captured, so a value that only <c>AddEmailParser</c>
    /// checked could be re-armed after the check. The unclamped value is still remembered (see
    /// <see cref="RequestedMaxEmbeddedDepth"/>) so <c>AddEmailParser</c> can throw on it instead
    /// of clamping silently. Negative values are not clamped: they are harmless at parse time
    /// (every embedded message is skipped) and <c>AddEmailParser</c> rejects them.
    /// </remarks>
    public int MaxEmbeddedDepth
    {
        get => _maxEmbeddedDepth;
        set
        {
            RequestedMaxEmbeddedDepth = value;
            _maxEmbeddedDepth = value > MaxSupportedEmbeddedDepth ? MaxSupportedEmbeddedDepth : value;
        }
    }

    /// <summary>
    /// The last value assigned to <see cref="MaxEmbeddedDepth"/>, before clamping. Exists only
    /// so <c>AddEmailParser</c> can still see — and throw on — a request the setter has already
    /// made safe; nothing at parse time reads it.
    /// </summary>
    internal int RequestedMaxEmbeddedDepth { get; private set; } = DefaultEmbeddedDepth;

    /// <summary>
    /// Total number of embedded messages parsed per top-level document, across every branch
    /// and every nesting level. Caps the fan-out that <see cref="MaxEmbeddedDepth"/> alone
    /// leaves open (a message with a thousand embedded messages is only one level deep).
    /// </summary>
    public int MaxEmbeddedMessages { get; set; } = 50;
}
