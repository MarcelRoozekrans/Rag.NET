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
    /// <b>There are two recursion paths, and only one of them leaves this assembly.</b> A
    /// nested <c>message/rfc822</c> part arrives as a live <c>MimeKit.MessagePart</c> and is
    /// parsed <i>in place</i>: <c>ParseMessageAsync → ParseAttachmentsAsync →
    /// ParseEmbeddedAsync → ParseMessageAsync</c>, four async-iterator frames per level, every
    /// one of them ours, with <see cref="EmailAttachmentDispatcher"/> never involved at all.
    /// That is the path the ~500-level measurement was taken on — raw nested
    /// <c>message/rfc822</c> is the ~81-bytes-per-level construction that reaches such depths
    /// cheaply. The second path is a message-typed <i>stream</i> attachment (an <c>.eml</c>
    /// carrying a <c>.msg</c>), which runs through <see cref="EmailAttachmentDispatcher"/>: it
    /// selects a parser by <b>content type</b> and re-enters through the public
    /// <see cref="Abstractions.IDocumentParser"/> boundary. That indirection is deliberate — it
    /// replaced a <c>ReferenceEquals(parser, self)</c> check that missed
    /// <c>.eml → .msg → .eml</c> chains entirely, because consecutive levels there are handled
    /// by <i>different</i> parser instances.
    /// </para>
    /// <para>
    /// <b>The ceiling stays regardless of how the traversal is written.</b> Draining an
    /// explicit <c>Stack</c> of section enumerators depth-first would remove the overflow class
    /// outright for the in-place path, and for the dispatcher path in every configuration this
    /// repository ships — the parser resolved for a message content type <i>is</i> one of these
    /// two. What would survive is a bound on a third-party parser registered for a message
    /// content type, whose frames are genuinely not ours to unwind. So the ceiling narrows in
    /// scope but does not go away, and the cost is a second traversal path in a parser that
    /// currently has one. Not done here because the ceiling holds on its own numbers; recorded
    /// as deferred work rather than a closed question (roadmap Phase 3.9), because the earlier
    /// claim that an explicit stack <i>cannot</i> help was wrong.
    /// </para>
    /// <para>
    /// Two measured numbers make 64 defensible on their own: the ceiling is <b>64</b> and the
    /// overflow floor is <b>~500</b>. An order of magnitude separates them, so no input that
    /// reaches the bound can overflow — which is the whole argument, and it does not depend on
    /// how deep real mail runs. Real forwarded-mail chains are expected to be far shallower
    /// still, but that is an expectation and not a measurement: nothing here has counted them.
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
