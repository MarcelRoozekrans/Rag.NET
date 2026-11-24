using System.Text.Json.Serialization;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Api.Contracts;

/// <summary>
/// <c>RagOptions</c> on the wire. Every field is optional: one that is left out takes the value
/// the in-process <c>RagPipeline</c> uses when the caller does not set it.
/// </summary>
/// <remarks>
/// <para>
/// As on <c>RagOptions</c>, retrieval is set either with the short-form fields, <see cref="TopK"/>
/// to <see cref="MetadataFilter"/>, or with <see cref="Retrieval"/>, never both. The server rejects
/// a request that sets both with 400, naming the fields.
/// </para>
/// <para>
/// The prompt-shaping fields, <see cref="SystemPrompt"/>, <see cref="Temperature"/>,
/// <see cref="ConversationHistory"/> and the prompt templates in <see cref="MapReduceOptions"/> and
/// <see cref="RefineOptions"/>, are rejected with 400 unless the server sets
/// <c>RagApiOptions.AllowClientPromptOverrides</c>. Retrieval fields are always accepted.
/// </para>
/// </remarks>
public sealed record RagOptionsDto
{
    /// <summary>Mirrors <c>RagOptions.TopK</c>.</summary>
    public int? TopK { get; init; }

    /// <summary>Mirrors <c>RagOptions.MaxContextTokens</c>.</summary>
    public int? MaxContextTokens { get; init; }

    /// <summary>Mirrors <c>RagOptions.MinScore</c>.</summary>
    public double? MinScore { get; init; }

    /// <summary>Mirrors <c>RagOptions.UseHybridSearch</c>. Left out means <see langword="false"/>.</summary>
    public bool? UseHybridSearch { get; init; }

    /// <summary>Mirrors <c>RagOptions.UseLostInTheMiddleReordering</c>.</summary>
    public bool? UseLostInTheMiddleReordering { get; init; }

    /// <summary>Mirrors <c>RagOptions.UseRedundancyFilter</c>.</summary>
    public bool? UseRedundancyFilter { get; init; }

    /// <summary>Mirrors <c>RagOptions.RedundancyThreshold</c>.</summary>
    public float? RedundancyThreshold { get; init; }

    /// <summary>Mirrors <c>RagOptions.MetadataFilter</c>. Values keep their kind on the wire.</summary>
    public IDictionary<string, MetadataValue>? MetadataFilter { get; init; }

    /// <summary>Mirrors <c>RagOptions.Retrieval</c>: the full retrieval options for this call.</summary>
    public RetrievalOptionsDto? Retrieval { get; init; }

    /// <summary>Mirrors <c>RagOptions.SystemPrompt</c>. Prompt-shaping.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Mirrors <c>RagOptions.SendSourcesAsToolResult</c>.</summary>
    public bool? SendSourcesAsToolResult { get; init; }

    /// <summary>Mirrors <c>RagOptions.Temperature</c>. Prompt-shaping.</summary>
    public float? Temperature { get; init; }

    /// <summary>Mirrors <c>RagOptions.ConversationHistory</c>, text turns only. Prompt-shaping.</summary>
    public IReadOnlyList<ChatMessageDto>? ConversationHistory { get; init; }

    /// <summary>Mirrors <c>RagOptions.SynthesisStrategy</c>, by name.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<SynthesisStrategy>))]
    public SynthesisStrategy? SynthesisStrategy { get; init; }

    /// <summary>Mirrors <c>RagOptions.MapReduceOptions</c>.</summary>
    public MapReduceOptionsDto? MapReduceOptions { get; init; }

    /// <summary>Mirrors <c>RagOptions.RefineOptions</c>.</summary>
    public RefineOptionsDto? RefineOptions { get; init; }

    /// <summary>Mirrors <c>RagOptions.SkipCompression</c>.</summary>
    public bool? SkipCompression { get; init; }
}
