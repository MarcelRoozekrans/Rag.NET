using Microsoft.Extensions.AI;
using Rag.NET.Api.Contracts;
using Rag.NET.Models.Options;

namespace Rag.NET.Api.Mapping;

/// <summary>
/// Turns the wire options of a request into the options the pipeline takes (#944). A field the
/// request leaves out is left unset, so it takes the value the in-process <c>RagPipeline</c> uses
/// when a caller does not set it. A request without options passes <see langword="null"/>, as an
/// in-process caller would.
/// </summary>
internal static class RagOptionsMapper
{
    private static readonly RetrievalOptions RetrievalDefaults = new();
    private static readonly EnsembleOptions EnsembleDefaults = new();
    private static readonly MapReduceOptions MapReduceDefaults = new();

    /// <summary>
    /// Returns why the request's options are rejected, or <see langword="null"/> when they are
    /// accepted. Each reason names the fields, by their JSON path.
    /// </summary>
    /// <param name="options">The request's options.</param>
    /// <param name="allowPromptOverrides"><see cref="DependencyInjection.RagApiOptions.AllowClientPromptOverrides"/>.</param>
    internal static string? Reject(RagOptionsDto? options, bool allowPromptOverrides)
    {
        if (options is null)
            return null;

        var promptFields = PromptShapingFields(options);
        if (promptFields.Count > 0 && !allowPromptOverrides)
        {
            return $"{string.Join(", ", promptFields)} shape the prompt, and this server does not let "
                + "clients set them. Leave them out, or enable RagApiOptions.AllowClientPromptOverrides on the server.";
        }

        if (options.ConversationHistory is { } history)
        {
            for (var i = 0; i < history.Count; i++)
            {
                if (history[i] is null || string.IsNullOrWhiteSpace(history[i].Role) || history[i].Text is null)
                    return $"options.conversationHistory[{i}] needs a role and a text.";
            }
        }

        var shortForm = ShortFormFields(options);
        if (options.Retrieval is not null && shortForm.Count > 0)
        {
            return $"options.retrieval was set together with {string.Join(", ", shortForm)}. "
                + "Retrieval replaces those fields; set their values on options.retrieval instead.";
        }

        return null;
    }

    /// <summary>The <see cref="RagOptions"/> for an accepted request; see <see cref="Reject"/>.</summary>
    /// <param name="options">The request's options.</param>
    internal static RagOptions? ToRagOptions(RagOptionsDto? options)
    {
        if (options is null)
            return null;

        var result = new RagOptions
        {
            Retrieval = ToRetrievalOptions(options.Retrieval),
            SystemPrompt = options.SystemPrompt,
            Temperature = options.Temperature,
            ConversationHistory = options.ConversationHistory is { Count: > 0 } history
                ? history.Select(m => new ChatMessage(new ChatRole(m.Role), m.Text)).ToList()
                : null,
            MapReduceOptions = ToMapReduceOptions(options.MapReduceOptions),
            RefineOptions = options.RefineOptions is { } refine
                ? new RefineOptions { InitialPromptTemplate = refine.InitialPromptTemplate, RefinePromptTemplate = refine.RefinePromptTemplate }
                : null,
        };

        if (options.SendSourcesAsToolResult is { } sendSources) result.SendSourcesAsToolResult = sendSources;
        if (options.SynthesisStrategy is { } strategy) result.SynthesisStrategy = strategy;
        if (options.SkipCompression is { } skipCompression) result.SkipCompression = skipCompression;

        // Assigned only when sent: assigning one marks it set, and RagPipeline rejects a set
        // short-form field next to Retrieval.
        if (options.TopK is { } topK) result.TopK = topK;
        if (options.MaxContextTokens is { } maxContextTokens) result.MaxContextTokens = maxContextTokens;
        if (options.MinScore is { } minScore) result.MinScore = minScore;
        if (options.UseHybridSearch is { } useHybrid) result.UseHybridSearch = useHybrid;
        if (options.UseLostInTheMiddleReordering is { } reorder) result.UseLostInTheMiddleReordering = reorder;
        if (options.UseRedundancyFilter is { } redundancy) result.UseRedundancyFilter = redundancy;
        if (options.RedundancyThreshold is { } threshold) result.RedundancyThreshold = threshold;
        if (options.MetadataFilter is { } filter) result.MetadataFilter = filter;

        return result;
    }

    /// <summary>The <see cref="RetrievalOptions"/> for a request; <see langword="null"/> when it sent none.</summary>
    /// <param name="options">The request's retrieval options.</param>
    internal static RetrievalOptions? ToRetrievalOptions(RetrievalOptionsDto? options)
    {
        if (options is null)
            return null;

        var d = RetrievalDefaults;
        return new RetrievalOptions
        {
            TopK = options.TopK ?? d.TopK,
            MinScore = options.MinScore ?? d.MinScore,
            MetadataFilter = options.MetadataFilter ?? d.MetadataFilter,
            UseHybridSearch = options.UseHybridSearch ?? d.UseHybridSearch,
            EnsembleOptions = ToEnsembleOptions(options.EnsembleOptions),
            UseSparseSearch = options.UseSparseSearch ?? d.UseSparseSearch,
            UseLostInTheMiddleReordering = options.UseLostInTheMiddleReordering ?? d.UseLostInTheMiddleReordering,
            UseRedundancyFilter = options.UseRedundancyFilter ?? d.UseRedundancyFilter,
            RedundancyThreshold = options.RedundancyThreshold ?? d.RedundancyThreshold,
            MaxContextTokens = options.MaxContextTokens ?? d.MaxContextTokens,
            UseMmr = options.UseMmr ?? d.UseMmr,
            MmrLambda = options.MmrLambda ?? d.MmrLambda,
            MmrCandidateCount = options.MmrCandidateCount ?? d.MmrCandidateCount,
            UseMultiQuery = options.UseMultiQuery ?? d.UseMultiQuery,
            UseReranking = options.UseReranking ?? d.UseReranking,
            CandidateCount = options.CandidateCount ?? d.CandidateCount,
            UseHyde = options.UseHyde ?? d.UseHyde,
            UseAdaptiveRetrieval = options.UseAdaptiveRetrieval ?? d.UseAdaptiveRetrieval,
            UseCrag = options.UseCrag ?? d.UseCrag,
            CragScoreThreshold = options.CragScoreThreshold ?? d.CragScoreThreshold,
            CragFallbackMode = options.CragFallbackMode ?? d.CragFallbackMode,
            UseSelfQuery = options.UseSelfQuery ?? d.UseSelfQuery,
            UseCacheEmbedding = options.UseCacheEmbedding ?? d.UseCacheEmbedding,
            UseCacheResult = options.UseCacheResult ?? d.UseCacheResult,
            UseParentDocument = options.UseParentDocument ?? d.UseParentDocument,
            UseTagRetrieval = options.UseTagRetrieval ?? d.UseTagRetrieval,
            UseTimeWeighting = options.UseTimeWeighting ?? d.UseTimeWeighting,
        };
    }

    private static EnsembleOptions? ToEnsembleOptions(EnsembleOptionsDto? options) => options is null
        ? null
        : new EnsembleOptions
        {
            DenseWeight = options.DenseWeight ?? EnsembleDefaults.DenseWeight,
            Bm25Weight = options.Bm25Weight ?? EnsembleDefaults.Bm25Weight,
            SparseWeight = options.SparseWeight ?? EnsembleDefaults.SparseWeight,
            K = options.K ?? EnsembleDefaults.K,
        };

    private static MapReduceOptions? ToMapReduceOptions(MapReduceOptionsDto? options) => options is null
        ? null
        : new MapReduceOptions
        {
            MapConcurrency = options.MapConcurrency ?? MapReduceDefaults.MapConcurrency,
            MapPromptTemplate = options.MapPromptTemplate,
            ReducePromptTemplate = options.ReducePromptTemplate,
        };

    // The fields that replace or steer the prompt the server's answer engine sends: the three
    // #944 names, and the map-reduce and refine templates, which replace prompt text outright.
    private static List<string> PromptShapingFields(RagOptionsDto options)
    {
        var fields = new List<string>();
        if (options.SystemPrompt is not null) fields.Add("options.systemPrompt");
        if (options.Temperature is not null) fields.Add("options.temperature");
        if (options.ConversationHistory is { Count: > 0 }) fields.Add("options.conversationHistory");
        if (options.MapReduceOptions?.MapPromptTemplate is not null) fields.Add("options.mapReduceOptions.mapPromptTemplate");
        if (options.MapReduceOptions?.ReducePromptTemplate is not null) fields.Add("options.mapReduceOptions.reducePromptTemplate");
        if (options.RefineOptions?.InitialPromptTemplate is not null) fields.Add("options.refineOptions.initialPromptTemplate");
        if (options.RefineOptions?.RefinePromptTemplate is not null) fields.Add("options.refineOptions.refinePromptTemplate");
        return fields;
    }

    private static List<string> ShortFormFields(RagOptionsDto options)
    {
        var fields = new List<string>();
        if (options.TopK is not null) fields.Add("options.topK");
        if (options.MaxContextTokens is not null) fields.Add("options.maxContextTokens");
        if (options.MinScore is not null) fields.Add("options.minScore");
        if (options.UseHybridSearch is not null) fields.Add("options.useHybridSearch");
        if (options.UseLostInTheMiddleReordering is not null) fields.Add("options.useLostInTheMiddleReordering");
        if (options.UseRedundancyFilter is not null) fields.Add("options.useRedundancyFilter");
        if (options.RedundancyThreshold is not null) fields.Add("options.redundancyThreshold");
        if (options.MetadataFilter is not null) fields.Add("options.metadataFilter");
        return fields;
    }
}
