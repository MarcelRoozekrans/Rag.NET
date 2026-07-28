using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.Evaluation;

/// <summary>One side of an A/B comparison.</summary>
/// <param name="Name">Label used in the report. Must be unique within a comparison.</param>
/// <param name="Pipeline">
/// The configured pipeline. Differences here — chunking, vector store, embedding model, reranker —
/// are what this framework exists to compare; a variant is a whole pipeline rather than a bag of
/// per-call settings for exactly that reason.
/// </param>
/// <param name="Options">Per-call settings for this variant (TopK, prompt, temperature).</param>
/// <param name="CostLedger">
/// Optional ledger <b>dedicated to this variant's pipeline</b>.
/// <para>
/// The harness receives pipelines that are already built, so it cannot instrument their spend. All
/// it can do is read a ledger the caller wired into that pipeline itself — snapshot
/// <see cref="ICostLedger.GetSpendAsync"/> over <see cref="Rag.NET.Models.CostWindow.Day"/> before
/// and after the run and report the difference. Any other traffic writing to the same ledger on the
/// same UTC day lands in that difference, so a ledger shared with production, with the other
/// variant, or with a dataset build measures those too and reports the total as this variant's
/// cost.
/// </para>
/// <para>
/// <b>A run that crosses UTC midnight reports no cost for that variant.</b> The window is the
/// current UTC calendar day, so the bucket empties between the two readings and the second comes
/// back lower than the first. The difference is then a negative number, or at best today's spend
/// minus yesterday's — neither of which is what this variant spent. An A/B run is two pipelines
/// over the whole dataset plus a judge pass, so crossing midnight is ordinary rather than exotic.
/// Where a shared ledger makes the figure <i>too large</i>, which the caveat above describes, a
/// rollover makes it meaningless, so it is dropped: the variant is absent from
/// <c>AbReport.Cost</c>, exactly as if no ledger had been supplied. Re-run within one UTC day to
/// measure it.
/// </para>
/// <para>
/// Omit it and cost is reported as absent rather than as zero. An unexplained cost column that
/// silently includes unrelated traffic is the kind of plausible number this milestone exists to
/// remove.
/// </para>
/// </param>
public sealed record AbVariant(
    string Name,
    IRagPipeline Pipeline,
    RagOptions? Options = null,
    ICostLedger? CostLedger = null);
