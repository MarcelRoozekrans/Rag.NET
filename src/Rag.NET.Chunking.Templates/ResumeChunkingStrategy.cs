using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rag.NET.Chunking.Templates;

public sealed partial class ResumeChunkingStrategy(
    IChatClient chatClient,
    ResumeChunkingOptions options,
    ILogger<ResumeChunkingStrategy>? logger = null) : IDocumentChunkingStrategy
{
    private readonly ILogger<ResumeChunkingStrategy> _logger = logger ?? NullLogger<ResumeChunkingStrategy>.Instance;

    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fullText = new System.Text.StringBuilder();
        DocumentId? docId = null;
        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            docId ??= section.DocumentId;
            if (fullText.Length > 0) fullText.Append("\n\n");
            fullText.Append(section.Text);
        }

        var id = docId ?? new DocumentId("unknown");
        var text = fullText.ToString();

        var activeClient = options.ChatClient ?? chatClient;
        var prompt = options.Prompt.Replace("{text}", text, StringComparison.Ordinal);

        JsonNode? parsed = null;
        try
        {
            var response = await activeClient
                .GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            parsed = JsonNode.Parse(response.Text ?? string.Empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogResumeParseFailure(_logger, id.Value, ex);
        }

        if (parsed is null)
        {
            yield return MakeChunk(text, id, 0, "full_text");
            yield break;
        }

        var index = 0;

        if (TryGetString(parsed["contact_info"]) is { Length: > 0 } contact)
            yield return MakeChunk(contact, id, index++, "contact_info");

        if (parsed["work_history"] is JsonArray workHistory)
            foreach (var job in workHistory)
            {
                var jobText = FormatObject(job);
                if (!string.IsNullOrWhiteSpace(jobText))
                    yield return MakeChunk(jobText, id, index++, "work_history");
            }

        if (parsed["education"] is JsonArray education)
            foreach (var edu in education)
            {
                var eduText = FormatObject(edu);
                if (!string.IsNullOrWhiteSpace(eduText))
                    yield return MakeChunk(eduText, id, index++, "education");
            }

        if (TryGetString(parsed["skills"]) is { Length: > 0 } skills)
            yield return MakeChunk(skills, id, index++, "skills");
    }

    /// <summary>
    /// Safely extracts a string value from a <see cref="JsonNode"/>.
    /// Returns <c>null</c> if the node is <c>null</c>, an array, an object,
    /// or any non-string scalar — guarding against LLM responses that return
    /// unexpected types for fields declared as strings in the prompt.
    /// </summary>
    private static string? TryGetString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static TextChunk MakeChunk(string text, DocumentId docId, int index, string section) =>
        new()
        {
            Text = text,
            DocumentId = docId,
            ChunkIndex = index,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["template"] = "resume",
                ["section"] = section,
            },
        };

    private static string FormatObject(JsonNode? node)
    {
        if (node is null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        if (node is JsonObject obj)
            foreach (var prop in obj)
                sb.AppendLine($"{prop.Key}: {prop.Value}");
        return sb.ToString().Trim();
    }

    [LoggerMessage(EventId = 1725293571, EventName = "log_resume_parse_failure", Level = LogLevel.Warning, Message = "Resume LLM call or JSON parse failed for document {DocumentId}; falling back to full-text chunk.")]
    private static partial void LogResumeParseFailure(ILogger logger, string documentId, Exception ex);
}
