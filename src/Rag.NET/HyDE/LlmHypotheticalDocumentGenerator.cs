using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.HyDE;

internal sealed class LlmHypotheticalDocumentGenerator(IChatClient chatClient, HydeOptions options) : IHypotheticalDocumentGenerator
{
    public async Task<string> GenerateAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var prompt = options.PromptTemplate
            .Replace("{query}", query, StringComparison.Ordinal);

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.Text ?? string.Empty;
    }
}
