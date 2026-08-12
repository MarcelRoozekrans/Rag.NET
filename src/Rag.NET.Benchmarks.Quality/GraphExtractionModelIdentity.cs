using System.Globalization;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The one definition of the identity string every <see cref="GraphExtractionCache"/> key is salted
/// with: <c>openai/gpt-4o-mini@t0.0</c>. The generation tool writes under it and the GraphRAG guard
/// reads under it, and it lives here — in the project both of them reference — precisely so neither
/// side hand-copies the string into a place where it can drift. A drifted copy does not fail
/// loudly: it computes keys no generation run ever wrote under, and the whole cache quietly misses.
/// </summary>
/// <remarks>
/// The sibling of <see cref="HypotheticalModelIdentity"/>, deliberately separate rather than shared.
/// The two caches hold different things generated at different temperatures for different runs, and
/// a single identity type would invite one of them to be changed for the other's reasons.
/// </remarks>
public static class GraphExtractionModelIdentity
{
    /// <summary>The exact model string the generation tool sends to OpenRouter.</summary>
    public const string ModelName = "openai/gpt-4o-mini";

    /// <summary>
    /// The temperature GraphRAG extraction is generated at: <b>0</b>.
    /// </summary>
    /// <remarks>
    /// The opposite choice from HyDE's, for the opposite reason, and the contrast is worth keeping
    /// in one place. HyDE samples three hypotheses per query at 0.8 <i>because</i> it averages
    /// them, and averaging three near-identical passages is a mean-of-1 nobody ships. Extraction
    /// averages nothing: one call, one JSON object of entities and relationships, and every extra
    /// degree of sampling freedom is another chance for a chunk to contribute an entity name that
    /// does not match the one the neighbouring chunk produced — which is the difference between a
    /// connected graph and sixty islands. Determinism still comes from the cache, not from the
    /// sampler; 0 is chosen for the extraction quality, not to make the run reproducible.
    /// </remarks>
    public const float ExtractionTemperature = 0f;

    /// <summary>
    /// Builds the identity: the model string and the sampling temperature, as
    /// <c>openai/gpt-4o-mini@t0.0</c>.
    /// </summary>
    /// <param name="temperature">
    /// The temperature the extractions were sampled at — normally
    /// <see cref="ExtractionTemperature"/>.
    /// </param>
    /// <returns>The identity string, invariant-formatted.</returns>
    /// <remarks>
    /// <para>
    /// <b>Temperature is in the identity because it changes the output distribution.</b> A review of
    /// the hypothetical cache found exactly this defect one component over: with the model string
    /// alone in the key, re-sampling at a different temperature hits every existing entry and
    /// silently serves text drawn from different settings, with nothing anywhere to say so.
    /// Anything else that moves the distribution — a top-p, a seed, a system message — belongs here
    /// too, and the run that adds it regenerates rather than reuses.
    /// </para>
    /// <para>
    /// <b>The format is explicit, and that is not fussiness.</b> The sibling identity interpolates
    /// the <see langword="float"/> directly, which renders 0.8 as <c>0.8</c> but would render 0 as
    /// <c>t0</c> — an identity that reads like "no temperature recorded" rather than "temperature
    /// zero", and one that would collide with a hypothetical future integer-valued setting. One
    /// decimal place minimum, more only if a temperature needs them.
    /// </para>
    /// </remarks>
    public static string For(float temperature) =>
        ModelName + "@t" + temperature.ToString("0.0###", CultureInfo.InvariantCulture);
}
