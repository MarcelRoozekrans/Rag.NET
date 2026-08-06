namespace Rag.NET.Models.Options;

/// <summary>
/// Selects which answer-generation engine <see cref="RagOptions.SynthesisStrategy"/> routes a call
/// to. Determines whether <see cref="RagOptions.MapReduceOptions"/> or
/// <see cref="RagOptions.RefineOptions"/> is read — the other is ignored regardless of strategy.
/// </summary>
public enum SynthesisStrategy
{
    /// <summary>A single chat completion over all retrieved sources at once.</summary>
    Default,

    /// <summary>
    /// Each source chunk is answered independently (the map step, concurrency controlled by
    /// <see cref="Rag.NET.Models.Options.MapReduceOptions.MapConcurrency"/>), then the partial
    /// answers are combined into one answer (the reduce step).
    /// </summary>
    MapReduce,

    /// <summary>
    /// An initial answer is generated from the first source chunk, then iteratively refined
    /// against each remaining chunk in turn.
    /// </summary>
    Refine,

    /// <summary>
    /// Forward-Looking Active Retrieval: generates sentence by sentence, triggering a
    /// mid-generation lookahead retrieval whenever confidence in the next sentence is low. Tuned
    /// by <c>FlareOptions</c>. Requires <c>UseFlare()</c> registration (retriever and confidence
    /// scorer); requesting this strategy without it throws <see cref="InvalidOperationException"/>.
    /// Unlike the other strategies, FLARE does not consult conversation memory — routing here
    /// drops <see cref="RagOptions.ConversationHistory"/> from the prompt.
    /// </summary>
    Flare
}
