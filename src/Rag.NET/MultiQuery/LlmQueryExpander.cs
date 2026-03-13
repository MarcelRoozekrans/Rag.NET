using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.MultiQuery;

internal sealed class LlmQueryExpander(IChatClient chatClient, MultiQueryOptions options) : IQueryExpander
{
    public async Task<IReadOnlyList<string>> ExpandAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var prompt = options.PromptTemplate
            .Replace("{count}", count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{query}", query, StringComparison.Ordinal);

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);

        return (response.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(count)
            .ToList();
    }
}
