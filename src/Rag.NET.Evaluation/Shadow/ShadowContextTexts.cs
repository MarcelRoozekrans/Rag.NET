using Rag.NET.Models;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>Projects a response's sources into the context texts a capture stores.</summary>
/// <remarks>
/// Prefers <see cref="SearchResult.CompressedText"/>, exactly as the answer engines do when they
/// build the prompt: when compression ran, the compressed view is the text the answer was
/// actually grounded in, and capturing the original chunk would misrepresent the run to the
/// offline scorer.
/// </remarks>
internal static class ShadowContextTexts
{
    internal static IReadOnlyList<string> From(IReadOnlyList<SearchResult> sources)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        var texts = new string[sources.Count];
        for (var i = 0; i < sources.Count; i++)
        {
            texts[i] = sources[i].CompressedText ?? sources[i].Chunk.Text;
        }

        return texts;
    }
}
