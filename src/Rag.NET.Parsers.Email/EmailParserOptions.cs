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
    /// <b>The in-place path is not recursive.</b> A nested <c>message/rfc822</c> arrives as a
    /// live <c>MimeKit.MessagePart</c>, and a nested <c>.msg</c> as a live
    /// <c>MsgReader.Outlook.Storage.Message</c>; both are walked by
    /// <see cref="EmbeddedTraversal"/>, which drains an explicit <c>Stack</c> of frames
    /// depth-first. CLR stack depth there is constant regardless of nesting — depth costs heap —
    /// so this ceiling is not what keeps that path from overflowing. Nothing does, because it
    /// cannot.
    /// </para>
    /// <para>
    /// <b>What the ceiling does bound.</b> A message-typed <i>stream</i> attachment (an
    /// <c>.eml</c> carrying a <c>.msg</c>) runs through <see cref="EmailAttachmentDispatcher"/>,
    /// which selects a parser by <b>content type</b> and re-enters through the public
    /// <see cref="Abstractions.IDocumentParser"/> boundary. That indirection is deliberate — it
    /// replaced a <c>ReferenceEquals(parser, self)</c> check that missed
    /// <c>.eml → .msg → .eml</c> chains entirely, because consecutive levels there are handled
    /// by <i>different</i> parser instances. In every configuration this repository ships the
    /// parser resolved for a message content type <i>is</i> one of these two, so the hop costs a
    /// bounded handful of frames per level and unwinds as each level finishes. The case that
    /// genuinely needs a bound is a <b>third-party parser registered for a message content
    /// type</b>, whose frames are not ours to unwind and whose per-level cost is unknown to us.
    /// Beyond that the ceiling is a sanity limit on how much work one document may ask for —
    /// 64 levels of contexts, composed <c>parent.eml#child.eml</c> file names and metadata,
    /// alongside the fan-out cap in <see cref="MaxEmbeddedMessages"/>.
    /// </para>
    /// <para>
    /// <b>History, not a live property.</b> The traversal this replaced was stack-recursive:
    /// <c>ParseMessageAsync → ParseAttachmentsAsync → ParseEmbeddedAsync → ParseMessageAsync</c>,
    /// frames that were not unwound until the nested enumeration finished. Measured on it, 480
    /// levels survived and 500+ terminated the process with <c>0xC00000FD</c>
    /// (<c>STATUS_STACK_OVERFLOW</c>) — uncatchable — and about 40 KB of hand-crafted MIME
    /// reached 500 levels, at roughly 81 bytes per level. That <b>~500</b> was the original
    /// justification for 64, an order of magnitude below it. It is recorded here as the floor of
    /// a traversal that no longer exists (roadmap Phase 3.9), not as a property of this parser:
    /// the in-place path now has no such floor, and <c>EmbeddedTraversalTests</c> drives the
    /// driver 10,000 levels deep to say so. Real forwarded-mail chains are expected to be far
    /// shallower than 64, but that is an expectation and not a measurement: nothing here has
    /// counted them.
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
