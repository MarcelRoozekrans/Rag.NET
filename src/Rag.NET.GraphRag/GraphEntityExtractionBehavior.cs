using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Graph;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Telemetry;

namespace Rag.NET.GraphRag;

/// <summary>
/// Ingestion behavior that extracts entities and relationships from each chunk via LLM,
/// stores them in the graph store, and appends embedded entity/relationship chunks to the context.
/// </summary>
public sealed partial class GraphEntityExtractionBehavior : IIngestionBehavior
{
    /// <summary>
    /// What an unusable weight becomes: the value
    /// <see cref="GraphRagOptions.EntityExtractionPrompt"/>'s own schema asks the model for, and the
    /// value 99.99% of the real corpus's 147,021 relationships already carried.
    /// </summary>
    /// <remarks>
    /// Substituted rather than the relationship dropped. The endpoints and the description were
    /// never in question — only one scalar was — and <c>Leiden.BuildAdjacency</c> already discards
    /// 5,492 of the real corpus's relationships, 3.74%, because their endpoints name no extracted
    /// entity or name the same one twice. Adding a second silent way to lose an edge in order to
    /// fix a problem of silence would be the wrong trade, and the all-weights-at-1.0 arm of the
    /// measurement puts the cost of the substitution at 0.03 modularity points.
    /// </remarks>
    private const double DefaultRelationshipWeight = 1.0;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IChatClient _chatClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IGraphStore _graphStore;
    private readonly GraphRagOptions _options;
    private readonly ILogger _logger;

    public GraphEntityExtractionBehavior(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IGraphStore graphStore,
        GraphRagOptions options,
        ILogger<GraphEntityExtractionBehavior>? logger = null)
    {
        _chatClient = chatClient;
        _embedder = embedder;
        _graphStore = graphStore;
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx,
        CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (!_options.Enabled)
        {
            return await next(ctx, ct).ConfigureAwait(false);
        }

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graphrag.extract");
        activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);

        var client = _options.ExtractionChatClient ?? _chatClient;
        var documentId = ctx.Metadata.DocumentId.ToString();

        var allEntities = new List<GraphEntity>();
        var allRelationships = new List<GraphRelationship>();
        var weights = new RelationshipWeightAudit();

#pragma warning disable HLQ012 // CollectionsMarshal.AsSpan cannot cross await boundaries
        for (var i = 0; i < ctx.Chunks.Count; i++)
        {
            await ExtractFromChunkAsync(
                client, ctx.Chunks[i], documentId, allEntities, allRelationships, weights, ct).ConfigureAwait(false);
        }
#pragma warning restore HLQ012

        activity?.SetTag("graphrag.entity.count", allEntities.Count);
        activity?.SetTag("graphrag.relationship.count", allRelationships.Count);
        ReportWeightAudit(activity, weights, documentId);

        await EmbedEntitiesAsync(ctx, allEntities, ct).ConfigureAwait(false);
        await EmbedRelationshipsAsync(ctx, allEntities.Count, allRelationships, ct).ConfigureAwait(false);

        return await next(ctx, ct).ConfigureAwait(false);
    }

    private async Task ExtractFromChunkAsync(
        IChatClient client,
        TextChunk chunk,
        string documentId,
        List<GraphEntity> allEntities,
        List<GraphRelationship> allRelationships,
        RelationshipWeightAudit weights,
        CancellationToken ct)
    {
        var chunkId = $"{chunk.DocumentId}_{chunk.ChunkIndex}";

        var prompt = BuildExtractionPrompt(chunk.Text);
        var extraction = await ExtractAsync(client, prompt, ct).ConfigureAwait(false);
        if (extraction is null) return;

        var entities = new List<ExtractedEntity>(extraction.Entities);
        var relationships = new List<ExtractedRelationship>(extraction.Relationships);

        await PerformGleaningAsync(client, chunk.Text, entities, relationships, ct).ConfigureAwait(false);

        var graphEntities = ConvertEntities(entities, documentId, chunkId);
        var graphRelationships = ConvertRelationships(relationships, _options, documentId, weights);

        if (graphEntities.Count > 0)
        {
            await _graphStore.AddEntitiesAsync(graphEntities, ct).ConfigureAwait(false);
            allEntities.AddRange(graphEntities);
        }

        if (graphRelationships.Count > 0)
        {
            await _graphStore.AddRelationshipsAsync(graphRelationships, ct).ConfigureAwait(false);
            allRelationships.AddRange(graphRelationships);
        }
    }

    /// <summary>
    /// Renders the extraction prompt: type-guidance placeholders first, chunk text last, so a
    /// chunk that happens to contain a placeholder token is never re-substituted.
    /// </summary>
    private string BuildExtractionPrompt(string chunkText)
    {
        return _options.EntityExtractionPrompt
            .Replace("{entity_types}", EntityTypeGuidance())
            .Replace("{relationship_types}", RelationshipTypeGuidance())
            .Replace("{text}", chunkText);
    }

    private string EntityTypeGuidance()
    {
        return _options.EntityTypes is { Length: > 0 } types
            ? $"Entity \"type\" must be exactly one of: {string.Join(", ", types)}. " +
              "Do not extract entities of any other type."
            : "Entity types should be general categories like: Person, Organization, Location, Event, Concept, Technology, Document.";
    }

    private string RelationshipTypeGuidance()
    {
        return _options.RelationshipTypes is { Length: > 0 } types
            ? $"Relationship \"description\" must be exactly one of: {string.Join(", ", types)}. " +
              "Do not extract relationships of any other kind."
            : "Relationship descriptions should be concise verb phrases.";
    }

    /// <summary>
    /// The enforcement half of <see cref="GraphRagOptions.EntityTypes"/> and
    /// <see cref="GraphRagOptions.RelationshipTypes"/>: prompting steers the LLM, this filter
    /// guarantees the contract. Case-insensitive because LLMs do not reproduce casing reliably.
    /// A null or empty list allows everything.
    /// </summary>
    private static bool IsAllowedType(string[]? allowed, string value)
    {
        if (allowed is not { Length: > 0 }) return true;

        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private async Task PerformGleaningAsync(
        IChatClient client,
        string chunkText,
        List<ExtractedEntity> entities,
        List<ExtractedRelationship> relationships,
        CancellationToken ct)
    {
        for (int i = 0; i < _options.GleaningPasses; i++)
        {
            var previousJson = JsonSerializer.Serialize(new ExtractionResult
            {
                Entities = entities,
                Relationships = relationships,
            }, s_jsonOptions);

            var gleanPrompt = _options.GleaningPrompt
                .Replace("{text}", chunkText)
                .Replace("{previous}", previousJson);

            var gleaned = await ExtractAsync(client, gleanPrompt, ct).ConfigureAwait(false);
            if (gleaned is not null)
            {
                entities.AddRange(gleaned.Entities);
                relationships.AddRange(gleaned.Relationships);
            }
        }
    }

    private List<GraphEntity> ConvertEntities(
        List<ExtractedEntity> entities, string documentId, string chunkId)
    {
        var result = new List<GraphEntity>(entities.Count);
        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (string.IsNullOrWhiteSpace(e.Name)) continue;
            if (!IsAllowedType(_options.EntityTypes, e.Type)) continue;
            result.Add(new GraphEntity(e.Name, e.Type, TruncateDescription(e.Description))
            {
                SourceDocumentId = documentId,
                SourceChunkIds = [chunkId],
            });
        }

        return result;
    }

    /// <summary>
    /// Turns the model's relationships into graph edges, bounding the one field on them the model
    /// is free to invent: the weight.
    /// </summary>
    /// <remarks>
    /// Issue #209. Weights reach modularity's null model directly, so an unbounded one is not a bad
    /// number in a report — it is the clustering. See <see cref="RelationshipWeightAudit"/> for the
    /// measurement and <see cref="GraphRagOptions.MaxRelationshipWeight"/> for why the bound is a
    /// clamp rather than a rejection.
    /// </remarks>
    internal static List<GraphRelationship> ConvertRelationships(
        List<ExtractedRelationship> relationships,
        GraphRagOptions options,
        string documentId,
        RelationshipWeightAudit weights)
    {
        var result = new List<GraphRelationship>(relationships.Count);
        for (var i = 0; i < relationships.Count; i++)
        {
            var r = relationships[i];
            if (string.IsNullOrWhiteSpace(r.Source) || string.IsNullOrWhiteSpace(r.Target)) continue;
            if (!IsAllowedType(options.RelationshipTypes, r.Description)) continue;

            var weight = BoundWeight(r.Weight, options.MaxRelationshipWeight, weights);
            result.Add(new GraphRelationship(r.Source, r.Target, r.Description, weight)
            {
                SourceDocumentId = documentId,
            });
        }

        return result;
    }

    /// <summary>
    /// Brings one model-supplied weight into the range Leiden can use, recording what it had to do.
    /// </summary>
    /// <param name="weight">The weight exactly as the model returned it.</param>
    /// <param name="ceiling">
    /// <see cref="GraphRagOptions.MaxRelationshipWeight"/>, already validated finite and positive.
    /// </param>
    /// <param name="weights">The document's audit; every alteration is counted on it.</param>
    /// <returns>A finite weight in <c>(0, ceiling]</c>.</returns>
    /// <remarks>
    /// <b>The order of the two tests matters and the negated form is deliberate.</b>
    /// <c>weight is > 0.0</c> is false for <see cref="double.NaN"/>, which
    /// <c>!(weight &lt;= 0.0)</c> would not be — a NaN weight would then fall through to the clamp,
    /// where <c>NaN &gt; ceiling</c> is also false, and reach the graph untouched. That is exactly
    /// the class of silence this method exists to end.
    /// </remarks>
    private static double BoundWeight(double weight, double ceiling, RelationshipWeightAudit weights)
    {
        weights.Observe(weight);

        if (!double.IsFinite(weight) || weight is not > 0.0)
        {
            weights.RecordReplaced();
            return DefaultRelationshipWeight;
        }

        if (weight > ceiling)
        {
            weights.RecordClamped();
            return ceiling;
        }

        return weight;
    }

    private async Task EmbedEntitiesAsync(
        IngestionContext ctx, List<GraphEntity> entities, CancellationToken ct)
    {
        if (entities.Count == 0) return;

        var entityTexts = new List<string>(entities.Count);
        for (var i = 0; i < entities.Count; i++)
            entityTexts.Add(entities[i].Description);

        var entityEmbeddings = await _embedder.GenerateAsync(entityTexts, cancellationToken: ct).ConfigureAwait(false);

        for (int i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            ctx.EmbeddedChunks.Add(new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = entity.Description,
                    DocumentId = ctx.Metadata.DocumentId,
                    ChunkIndex = -(i + 1),
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["graph_type"] = "entity",
                        ["graph_entity_name"] = entity.Name,
                        ["graph_entity_type"] = entity.Type,
                    },
                },
                Embedding = entityEmbeddings[i].Vector,
            });
        }
    }

    private async Task EmbedRelationshipsAsync(
        IngestionContext ctx, int entityCount, List<GraphRelationship> relationships, CancellationToken ct)
    {
        if (relationships.Count == 0) return;

        var relTexts = new List<string>(relationships.Count);
        for (var i = 0; i < relationships.Count; i++)
            relTexts.Add(relationships[i].Description);

        var relEmbeddings = await _embedder.GenerateAsync(relTexts, cancellationToken: ct).ConfigureAwait(false);

        for (int i = 0; i < relationships.Count; i++)
        {
            var rel = relationships[i];
            ctx.EmbeddedChunks.Add(new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = rel.Description,
                    DocumentId = ctx.Metadata.DocumentId,
                    ChunkIndex = -(entityCount + i + 1),
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["graph_type"] = "relationship",
                        ["graph_source_entity"] = rel.SourceEntity,
                        ["graph_target_entity"] = rel.TargetEntity,
                    },
                },
                Embedding = relEmbeddings[i].Vector,
            });
        }
    }

    private async Task<ExtractionResult?> ExtractAsync(IChatClient client, string prompt, CancellationToken ct)
    {
        try
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: ct).ConfigureAwait(false);

            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text)) return null;

            // A preamble before the fence used to be fatal here: the local strip ran only when
            // the response STARTED with a fence, every llama-3.3-70b reply opened with prose,
            // and the JsonException below turned each one into a silent empty graph. The shared
            // extractor owns that lesson now — see LlmJsonExtractor's remarks.
            var json = LlmJsonExtractor.Extract(text, LlmJsonPayloadKind.Object);
            return JsonSerializer.Deserialize<ExtractionResult>(json, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            LogExtractionFailed(_logger, ex);
            return null;
        }
        catch (ArgumentOutOfRangeException ex)
            when (ex.Message.Contains("ChatFinishReason", StringComparison.Ordinal))
        {
            // The provider set finish_reason to something outside the OpenAI schema — "error" in
            // practice, which OpenRouter and other compatible gateways emit when the upstream
            // model call fails. The OpenAI SDK throws while deserialising, before any of this is
            // reachable, and as a bare ArgumentOutOfRangeException that reads like a caller bug.
            //
            // Treated exactly as an unparseable response: this chunk yields nothing and the
            // document carries on. It used to escape the whole ingestion, where the catch-all
            // reported it as RagError.StorageFailed and pointed the reader at the vector store,
            // which had done nothing wrong (issue #143). One failed call is not a failed
            // document — the gleaning loop is already built to accept a chunk yielding nothing.
            LogProviderReturnedError(_logger, ex);
            return null;
        }
    }

    /// <summary>
    /// Publishes what was done to this document's relationship weights: always as activity tags,
    /// and as a warning when anything was altered.
    /// </summary>
    /// <remarks>
    /// <b>Tagged unconditionally, logged only on change.</b> A span that carries the counters even
    /// when they are zero makes "this corpus needs no bounding" a positive observation rather than
    /// the absence of one — which is exactly what was missing while 92% of the graph sat in one
    /// community. A warning on every document, by contrast, would be one line per article on a
    /// corpus where nothing is wrong.
    /// </remarks>
    private void ReportWeightAudit(Activity? activity, RelationshipWeightAudit weights, string documentId)
    {
        activity?.SetTag("graphrag.relationship.weight.replaced", weights.Replaced);
        activity?.SetTag("graphrag.relationship.weight.clamped", weights.Clamped);
        activity?.SetTag("graphrag.relationship.weight.largest", weights.LargestWeight);

        if (weights.Altered)
        {
            LogWeightsAdjusted(
                _logger, documentId, weights.Replaced, weights.Clamped, weights.LargestWeight);
        }
    }

    private string TruncateDescription(string description)
    {
        return description.Length > _options.MaxEntityDescriptionLength
            ? description[.._options.MaxEntityDescriptionLength]
            : description;
    }

    [LoggerMessage(EventId = 1085291744, EventName = "log_weights_adjusted", Level = LogLevel.Warning, Message = "Relationship weights outside the usable range were adjusted while extracting {DocumentId}: {Replaced} replaced with 1.0 for not being a finite number greater than zero, {Clamped} clamped to GraphRagOptions.MaxRelationshipWeight; the largest weight the model returned was {LargestWeight}. Weights far above the schema's 1.0 dominate modularity's null model and collapse community detection into a single community")]
    private static partial void LogWeightsAdjusted(
        ILogger logger, string documentId, int replaced, int clamped, double largestWeight);

    [LoggerMessage(EventId = 1144623303, EventName = "log_extraction_failed", Level = LogLevel.Warning, Message = "Failed to parse entity extraction JSON from LLM response")]
    private static partial void LogExtractionFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 1793044612, EventName = "log_provider_returned_error", Level = LogLevel.Warning, Message = "The chat provider returned an error response (finish_reason outside the OpenAI schema) during entity extraction; this chunk contributes no entities and ingestion continues")]
    private static partial void LogProviderReturnedError(ILogger logger, Exception ex);
}
