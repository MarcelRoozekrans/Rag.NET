using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Cli;

/// <summary>
/// Runs the <c>query</c> command against an already-resolved <see cref="IRagPipeline"/>.
/// </summary>
/// <remarks>
/// Calls <see cref="IRagPipeline.RetrieveAsync"/>, not <c>AskAsync</c>: this command returns the
/// chunks retrieved for a question, not a chat-generated answer synthesised from them. That keeps
/// it a single call against an already-configured pipeline, matching the phase's scope discipline
/// rather than adding a second, larger surface (system prompts, conversation history, synthesis
/// strategy) this task does not ask for.
/// </remarks>
internal static class QueryCommand
{
    /// <summary>
    /// Embeds <paramref name="query"/> and returns the <paramref name="topK"/> most relevant
    /// chunks <paramref name="pipeline"/> retrieves for it.
    /// </summary>
    /// <exception cref="InvalidOperationException">Retrieval failed; the message names why.</exception>
    public static async Task<Outcome> RunAsync(
        IRagPipeline pipeline, string query, int topK, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var options = new RetrievalOptions { TopK = topK };
        var result = await pipeline.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Retrieval failed: {result.Error}");
        }

        var items = result.Value
            .Select(searchResult => new ResultItem(
                searchResult.Chunk.Text, searchResult.Score, searchResult.Chunk.Metadata))
            .ToArray();

        return new Outcome(query, items);
    }

    /// <summary>The chunks retrieved for <paramref name="Query"/>, most relevant first.</summary>
    public sealed record Outcome(string Query, IReadOnlyList<ResultItem> Results);

    /// <summary>One retrieved chunk, the score it was found with, and its metadata.</summary>
    public sealed record ResultItem(string Text, double Score, IDictionary<string, MetadataValue> Metadata);
}
