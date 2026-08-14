namespace Rag.NET.GraphRag;

/// <summary>
/// What extraction had to do to the relationship weights one document's chunks returned, so that
/// altering them is a reported event rather than a silent one.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the defect it records was invisible for the whole life of the package.</b>
/// The extraction prompt asks the model for <c>"weight": 1.0</c> and nothing checked what came back.
/// Over the full 609-article MultiHop-RAG corpus the model answered with acquisition prices —
/// <c>Microsoft -&gt; Mojang</c> at 2.5e9 and <c>Microsoft -&gt; Rare</c> at 3.75e8 — and those two
/// edges out of 147,021 carried 99.99% of the graph's total weight. Modularity's gain term is
/// <c>w − γ·k_i·k_c/2m</c>, so a total weight of 2,875,204,647 drove the penalty for an ordinary
/// pair to about 1e-9, every merge paid, and Leiden returned 57,484 of 62,392 entities — 92.13% —
/// in one community at modularity 0.0001. Nothing logged, nothing counted, nothing failed.
/// </para>
/// <para>
/// One instance per document, filled by
/// <see cref="GraphEntityExtractionBehavior.ConvertRelationships"/> and read back out on the
/// <c>ragnet.graphrag.extract</c> activity and in a warning. It is deliberately a tally rather than
/// a per-edge record: a corpus that trips this trips it on a handful of edges, and the counts plus
/// the largest weight seen are what identify which model and which prompt did it.
/// </para>
/// </remarks>
internal sealed class RelationshipWeightAudit
{
    /// <summary>
    /// Gets how many weights were not a usable number and were replaced with the schema's 1.0.
    /// </summary>
    /// <remarks>
    /// Non-finite or non-positive. <see cref="double.NaN"/> poisons every comparison it reaches, an
    /// infinity makes the null model's denominator infinite and every gain term zero, zero is not an
    /// edge, and a negative inverts the gain so that merging is rewarded without bound. Eight of the
    /// real corpus's 147,021 weights land here, the smallest of them −1.00.
    /// </remarks>
    public int Replaced { get; private set; }

    /// <summary>Gets how many weights exceeded the ceiling and were reduced to it.</summary>
    public int Clamped { get; private set; }

    /// <summary>
    /// Gets the largest weight the model returned before anything was done to it, or 0 when no
    /// relationship carried a finite positive weight.
    /// </summary>
    /// <remarks>
    /// The number that names the cause. "Clamped 2 weights" says a bound did its job; "clamped 2
    /// weights, the largest 2.5E+009" says the model answered the weight question with a dollar
    /// amount, which is a different thing to go and fix.
    /// </remarks>
    public double LargestWeight { get; private set; }

    /// <summary>Gets whether anything was altered at all.</summary>
    public bool Altered => Replaced > 0 || Clamped > 0;

    /// <summary>Records a weight that was replaced with the schema default.</summary>
    public void RecordReplaced() => Replaced++;

    /// <summary>Records a weight that was reduced to the ceiling.</summary>
    public void RecordClamped() => Clamped++;

    /// <summary>Notes a raw weight, keeping the largest one seen.</summary>
    /// <param name="weight">The weight exactly as the model returned it.</param>
    public void Observe(double weight)
    {
        if (weight > LargestWeight)
        {
            LargestWeight = weight;
        }
    }
}
