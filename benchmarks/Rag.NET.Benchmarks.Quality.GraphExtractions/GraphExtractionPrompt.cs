using Microsoft.Extensions.AI;

namespace Rag.NET.Benchmarks.Quality.GraphExtractions;

/// <summary>
/// Flattens a chat request into the one string <see cref="GraphExtractionCache"/> keys on.
/// <para>
/// <b>One renderer, because two would be a cache that misses for no visible reason.</b> Every
/// client that consults the cache — the one that fills it, and the dry pass that costs a run before
/// it starts — has to compute the key from the same bytes. A second flattening that differed by a
/// separator would report a full cache as empty, or an empty one as full, and the number a spend
/// decision rests on would be wrong in the expensive direction.
/// </para>
/// </summary>
public static class GraphExtractionPrompt
{
    /// <summary>Renders a request as <c>role: text</c> lines, newline separated.</summary>
    /// <param name="messages">The messages exactly as the behavior built them.</param>
    /// <returns>The string the cache key is computed over.</returns>
    /// <remarks>
    /// GraphRAG sends exactly one user message today, so this is the prompt with six characters in
    /// front of it. The role prefix and the loop are there anyway because the alternative — take
    /// the single message's text — would key a two-message conversation as its first message the
    /// day a system prompt is added, and serve that day's extractions out of the day before's
    /// cache.
    /// </remarks>
    public static string Render(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var rendered = new List<string>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            rendered.Add(messages[i].Role.ToString() + ": " + messages[i].Text);
        }

        return string.Join("\n", rendered);
    }
}
