using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Rag.NET.Chunking;

/// <summary>
/// LLM-driven chunking that decomposes document text into atomic, self-contained propositions.
/// The document is split into token-bounded passages; each passage is sent to an
/// <see cref="IChatClient"/> that returns a JSON array of proposition strings, and each
/// proposition becomes its own chunk. On LLM or parse failure the passage itself is emitted
/// as a single fallback chunk.
/// </summary>
public sealed partial class PropositionChunkingStrategy(
    IChatClient chatClient,
    PropositionChunkingOptions options,
    ILogger<PropositionChunkingStrategy>? logger = null) : IDocumentChunkingStrategy
{
    private static readonly Tokenizer Cl100kTokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    private readonly ILogger<PropositionChunkingStrategy> _logger = logger ?? NullLogger<PropositionChunkingStrategy>.Instance;

    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (text, docId) = await ConcatenateSectionsAsync(sections, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var activeClient = options.ChatClient ?? chatClient;
        var chunkIndex = 0;

        foreach (var (passage, start) in SplitIntoPassages(text))
        {
            var end = start + passage.Length;

            // yield return cannot appear inside try-with-catch, so the LLM call + parse are
            // isolated in a helper; a null/empty result signals fallback to the passage chunk.
            IReadOnlyList<string>? propositions = null;
            try
            {
                propositions = await ExtractPropositionsAsync(activeClient, passage, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogPropositionExtractionFailure(_logger, docId.Value, ex);
            }

            if (propositions is null || propositions.Count == 0)
            {
                yield return MakeChunk(passage.Trim(), docId, chunkIndex++, start, end, "passage");
                continue;
            }

            if (options.EmitParentPassages)
                yield return MakeChunk(passage.Trim(), docId, chunkIndex++, start, end, "passage");

            foreach (var proposition in propositions)
                yield return MakeChunk(proposition, docId, chunkIndex++, start, end, "proposition");
        }
    }

    private static async Task<(string Text, DocumentId Id)> ConcatenateSectionsAsync(
        IAsyncEnumerable<DocumentSection> sections,
        CancellationToken cancellationToken)
    {
        var fullText = new StringBuilder();
        DocumentId? docId = null;
        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            docId ??= section.DocumentId;
            if (fullText.Length > 0) fullText.Append("\n\n");
            fullText.Append(section.Text);
        }

        return (fullText.ToString(), docId ?? new DocumentId("unknown"));
    }

    /// <summary>
    /// Splits the full text into consecutive windows of at most
    /// <see cref="PropositionChunkingOptions.MaxPassageTokens"/> tokens (no overlap).
    /// The windows partition the token sequence, so the untrimmed decoded passages
    /// concatenate back to the full text — accumulating their lengths yields exact
    /// character spans without re-searching the source string.
    /// </summary>
    private List<(string Passage, int Start)> SplitIntoPassages(string text)
    {
        var ids = Cl100kTokenizer.EncodeToIds(text);
        var passages = new List<(string, int)>();
        // Reusable window buffer: Tokenizer.Decode only accepts IEnumerable<int>, and a
        // List<int> is a reference type — no per-iteration boxing or LINQ allocation.
        var window = new List<int>(Math.Min(options.MaxPassageTokens, ids.Count));
        var start = 0;
        for (var offset = 0; offset < ids.Count; offset += options.MaxPassageTokens)
        {
            var count = Math.Min(options.MaxPassageTokens, ids.Count - offset);
            window.Clear();
            for (var i = offset; i < offset + count; i++)
                window.Add(ids[i]);

            var passage = Cl100kTokenizer.Decode(window);
            passages.Add((passage, start));
            start += passage.Length;
        }

        return passages;
    }

    private async Task<IReadOnlyList<string>?> ExtractPropositionsAsync(
        IChatClient client,
        string passage,
        CancellationToken cancellationToken)
    {
        var response = await client
            .GetResponseAsync(BuildMessages(passage), options: null, cancellationToken)
            .ConfigureAwait(false);

        if (JsonNode.Parse(response.Text ?? string.Empty) is not JsonArray array)
            return null;

        var propositions = new List<string>();
        foreach (var node in array)
        {
            if (propositions.Count >= options.MaxPropositionsPerPassage)
                break;
            if (TryGetString(node) is { } value && !string.IsNullOrWhiteSpace(value))
                propositions.Add(value.Trim());
        }

        return propositions;
    }

    private static List<ChatMessage> BuildMessages(string passage)
    {
        // Per-call randomized delimiter suffix — defends against prompt-injection attempts
        // that embed a literal closing tag to escape the fence. Must be a v4 GUID: v7's
        // leading hex chars are timestamp-derived and predictable.
        var delim = Guid.NewGuid().ToString("N")[..8];

        return
        [
            new(ChatRole.System,
                "You decompose text into atomic propositions for a retrieval system. " +
                "Each proposition is a single, self-contained factual claim expressed as one complete sentence, " +
                "understandable without the surrounding text (resolve pronouns). " +
                "Return ONLY a JSON array of strings — no markdown, no commentary."),
            new(ChatRole.User, $"<content-{delim}>\n{passage}\n</content-{delim}>"),
        ];
    }

    /// <summary>
    /// Safely extracts a string value from a <see cref="JsonNode"/>. Returns <c>null</c> for
    /// nulls, arrays, objects, and non-string scalars — guarding against LLM responses that
    /// deviate from the requested array-of-strings shape.
    /// </summary>
    private static string? TryGetString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static TextChunk MakeChunk(string text, DocumentId docId, int index, int start, int end, string kind) =>
        new()
        {
            Text = text,
            DocumentId = docId,
            ChunkIndex = index,
            // Compatibility contract: StartPosition/EndPosition carry the SOURCE-PASSAGE char
            // span (not a span of the proposition text, which does not exist verbatim in the
            // source). The core ParentDocumentIngestionBehavior maps child chunks to parent
            // chunks via child.StartPosition, so proposition chunks must report their
            // passage's span for Parent Document Retrieval to resolve the right parent.
            StartPosition = start,
            EndPosition = end,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["parent.start"] = start.ToString(CultureInfo.InvariantCulture),
                ["parent.end"] = end.ToString(CultureInfo.InvariantCulture),
                ["chunk.kind"] = kind,
            },
        };

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Proposition extraction LLM call or JSON parse failed for document {DocumentId}; falling back to passage chunk.")]
    private static partial void LogPropositionExtractionFailure(ILogger logger, string documentId, Exception ex);
}
