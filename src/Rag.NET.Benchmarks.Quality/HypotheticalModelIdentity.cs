namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The one definition of the identity string every hypothetical cache key is salted with:
/// <c>openai/gpt-4o-mini@t0.8</c> at the library's default sampling temperature. The generation
/// tool writes under it and the ablation table's HyDE row reads under it, and it lives here — in
/// the project both of them already reference — precisely so neither side ever hand-copies the
/// string into a place where it can drift. A drifted copy does not fail loudly: it computes keys
/// no generation run ever wrote under, and the whole cache quietly misses.
/// </summary>
public static class HypotheticalModelIdentity
{
    /// <summary>The exact model string the generation tool sends to OpenRouter.</summary>
    public const string ModelName = "openai/gpt-4o-mini";

    /// <summary>
    /// Builds the identity: the model string and the sampling temperature, as
    /// <c>openai/gpt-4o-mini@t0.8</c>.
    /// </summary>
    /// <param name="hypothesisTemperature">
    /// The temperature the hypotheses were sampled at — pass <c>HydeOptions.HypothesisTemperature</c>
    /// from the same options object whose <c>PromptTemplate</c> is handed to the cache, so the two
    /// key components cannot come from different configurations.
    /// </param>
    /// <returns>The identity string, invariant-formatted.</returns>
    /// <remarks>
    /// <para>
    /// <b>Temperature belongs in the identity because it changes the output distribution.</b> The
    /// model string alone was the first version of this and it was wrong in the expensive
    /// direction: re-sampling at a different temperature would hit every existing entry and
    /// silently serve text drawn from different settings, with nothing anywhere to say so — the
    /// same failure the prompt template is in the key to prevent, one component over. Anything
    /// else that moves the distribution — a top-p, a seed, a system message — has to be added here
    /// too, and the run that adds it regenerates rather than reuses.
    /// </para>
    /// <para>
    /// Readable rather than hashed, because this string appears in every refuse-on-miss failure
    /// the table can produce, and "no cached hypothetical under openai/gpt-4o-mini@t0.8" is a
    /// message someone can act on.
    /// </para>
    /// </remarks>
    public static string For(float hypothesisTemperature) =>
        FormattableString.Invariant($"{ModelName}@t{hypothesisTemperature}");
}
