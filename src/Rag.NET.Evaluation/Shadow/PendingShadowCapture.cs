using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// One shadowed request the background consumer still owes a secondary run: the primary's side is
/// complete, the secondary's is a pipeline that has not been asked yet.
/// </summary>
/// <remarks>
/// <para>
/// This is what the request path enqueues, and its shape is the isolation contract in data form.
/// The decorator's job ends at handing over the question, the finished primary side, and the
/// pipeline to shadow — <b>running the secondary is the consumer's job</b>, because anything the
/// request path awaited would couple the primary's latency to the secondary's. A consequence
/// worth keeping: a pending item dropped by a full queue cost nothing — its secondary never ran —
/// whereas queueing completed pairs would have spent the secondary's money before every drop.
/// </para>
/// <para>
/// <see cref="Options"/> is the caller's, forwarded verbatim: the unit of comparison is a request,
/// so the secondary answers the same question under the same per-call settings, and the variants
/// differ only in how the two pipelines are built.
/// </para>
/// </remarks>
/// <param name="Question">The production question, exactly as the caller asked it.</param>
/// <param name="Options">The per-call settings the caller passed, forwarded to the secondary.</param>
/// <param name="Primary">The completed primary side the caller was served.</param>
/// <param name="SecondaryPipeline">The pipeline the consumer runs out-of-band.</param>
/// <param name="SecondaryVariantName">The label the secondary's side is captured under.</param>
/// <param name="CapturedAt">When the shadowed request happened, stamped on the request path.</param>
public sealed record PendingShadowCapture(
    string Question,
    RagOptions? Options,
    ShadowVariantCapture Primary,
    IRagPipeline SecondaryPipeline,
    string SecondaryVariantName,
    DateTimeOffset CapturedAt)
{
    /// <inheritdoc cref="PendingShadowCapture" path="/param[@name='Question']"/>
    public string Question { get; init; } =
        Question ?? throw new ArgumentNullException(nameof(Question));

    /// <inheritdoc cref="PendingShadowCapture" path="/param[@name='Primary']"/>
    public ShadowVariantCapture Primary { get; init; } =
        Primary ?? throw new ArgumentNullException(nameof(Primary));

    /// <inheritdoc cref="PendingShadowCapture" path="/param[@name='SecondaryPipeline']"/>
    public IRagPipeline SecondaryPipeline { get; init; } =
        SecondaryPipeline ?? throw new ArgumentNullException(nameof(SecondaryPipeline));

    /// <inheritdoc cref="PendingShadowCapture" path="/param[@name='SecondaryVariantName']"/>
    /// <remarks>
    /// <c>Primary</c> is safe to dereference in the helper: initializers run in declaration
    /// order, so a null primary has already thrown above — the same layout as
    /// <see cref="ShadowCapture"/>.
    /// </remarks>
    public string SecondaryVariantName { get; init; } = RequireDistinctName(Primary, SecondaryVariantName);

    /// <summary>Completes the pair once the consumer has run the secondary.</summary>
    /// <param name="secondary">What the secondary did: an answer or a recorded failure.</param>
    /// <returns>The finished capture, keyed to the production request it shadows.</returns>
    public ShadowCapture ToCapture(ShadowVariantCapture secondary) =>
        new(Question, Primary, secondary, CapturedAt);

    /// <summary>Refuses a blank secondary label, or one that collides with the primary's.</summary>
    /// <remarks>
    /// <see cref="ShadowCapture"/> refuses same-named sides too, but only when the pair is built —
    /// on the consumer, long after the request. Refusing at enqueue surfaces the misconfiguration
    /// where it was made rather than as a background persistence failure nobody is watching.
    /// </remarks>
    private static string RequireDistinctName(ShadowVariantCapture primary, string secondaryVariantName)
    {
        if (string.IsNullOrWhiteSpace(secondaryVariantName))
        {
            throw new ArgumentException(
                $"A pending capture needs a non-blank {nameof(SecondaryVariantName)}; the offline " +
                "scorer keys everything by it.", nameof(secondaryVariantName));
        }

        if (string.Equals(primary.VariantName, secondaryVariantName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Both sides of this pending capture would be labelled '{secondaryVariantName}'. " +
                "The offline scorer keys everything by variant name and refuses duplicates, so the " +
                "pair could never be scored.", nameof(secondaryVariantName));
        }

        return secondaryVariantName;
    }
}
