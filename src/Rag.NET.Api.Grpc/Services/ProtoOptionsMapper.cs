using Microsoft.Extensions.AI;
using Rag.NET.Api.Grpc.Proto;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Api.Grpc.Services;

/// <summary>
/// Turns the options of a request into the options the pipeline takes (#944). A field the request
/// leaves unset is left unset here, so it takes the value the in-process <c>RagPipeline</c> uses
/// when a caller does not set it. A request without options passes <see langword="null"/>, as an
/// in-process caller would.
/// </summary>
internal static class ProtoOptionsMapper
{
    private static readonly RetrievalOptions RetrievalDefaults = new();
    private static readonly EnsembleOptions EnsembleDefaults = new();
    private static readonly MapReduceOptions MapReduceDefaults = new();

    /// <summary>
    /// Returns why the request's options are rejected, or <see langword="null"/> when they are
    /// accepted. Each reason names the fields, by their proto path.
    /// </summary>
    /// <param name="options">The request's options.</param>
    /// <param name="allowPromptOverrides"><see cref="DependencyInjection.RagGrpcApiOptions.AllowClientPromptOverrides"/>.</param>
    internal static string? Reject(RagOptionsProto? options, bool allowPromptOverrides)
    {
        if (options is null)
            return null;

        var promptFields = PromptShapingFields(options);
        if (promptFields.Count > 0 && !allowPromptOverrides)
        {
            return $"{string.Join(", ", promptFields)} shape the prompt, and this server does not let "
                + "clients set them. Leave them out, or enable RagGrpcApiOptions.AllowClientPromptOverrides on the server.";
        }

        for (var i = 0; i < options.ConversationHistory.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(options.ConversationHistory[i].Role))
                return $"options.conversation_history[{i}] needs a role.";
        }

        var shortForm = ShortFormFields(options);
        if (options.Retrieval is not null && shortForm.Count > 0)
        {
            return $"options.retrieval was set together with {string.Join(", ", shortForm)}. "
                + "Retrieval replaces those fields; set their values on options.retrieval instead.";
        }

        if (options.HasSynthesisStrategy && !Enum.IsDefined(options.SynthesisStrategy))
            return "options.synthesis_strategy has a value this server does not know.";

        return RejectValues(options.MetadataFilter, "options.metadata_filter")
            ?? Reject(options.Retrieval, "options.retrieval");
    }

    /// <summary>
    /// Returns why the retrieval options are rejected, or <see langword="null"/> when they are
    /// accepted.
    /// </summary>
    /// <param name="options">The request's retrieval options.</param>
    /// <param name="path">The proto path of <paramref name="options"/>, for the message.</param>
    internal static string? Reject(RetrievalOptionsProto? options, string path)
    {
        if (options is null)
            return null;

        if (options.HasCragFallbackMode && !Enum.IsDefined(options.CragFallbackMode))
            return $"{path}.crag_fallback_mode has a value this server does not know.";

        return RejectValues(options.MetadataFilter, $"{path}.metadata_filter");
    }

    /// <summary>The <see cref="RagOptions"/> for an accepted request; see <see cref="Reject(RagOptionsProto?, bool)"/>.</summary>
    /// <param name="options">The request's options.</param>
    internal static RagOptions? ToRagOptions(RagOptionsProto? options)
    {
        if (options is null)
            return null;

        var result = new RagOptions
        {
            Retrieval = ToRetrievalOptions(options.Retrieval),
            SystemPrompt = options.HasSystemPrompt ? options.SystemPrompt : null,
            Temperature = options.HasTemperature ? options.Temperature : null,
            ConversationHistory = options.ConversationHistory.Count > 0
                ? options.ConversationHistory.Select(m => new ChatMessage(new ChatRole(m.Role), m.Text)).ToList()
                : null,
            MapReduceOptions = ToMapReduceOptions(options.MapReduceOptions),
            RefineOptions = options.RefineOptions is { } refine
                ? new RefineOptions
                {
                    InitialPromptTemplate = refine.HasInitialPromptTemplate ? refine.InitialPromptTemplate : null,
                    RefinePromptTemplate = refine.HasRefinePromptTemplate ? refine.RefinePromptTemplate : null,
                }
                : null,
        };

        if (options.HasSendSourcesAsToolResult) result.SendSourcesAsToolResult = options.SendSourcesAsToolResult;
        if (options.HasSkipCompression) result.SkipCompression = options.SkipCompression;
        if (options.HasSynthesisStrategy && ToSynthesisStrategy(options.SynthesisStrategy) is { } strategy)
            result.SynthesisStrategy = strategy;

        // Assigned only when sent: assigning one marks it set, and RagPipeline rejects a set
        // short-form field next to Retrieval.
        if (options.HasTopK) result.TopK = options.TopK;
        if (options.HasMaxContextTokens) result.MaxContextTokens = options.MaxContextTokens;
        if (options.HasMinScore) result.MinScore = options.MinScore;
        if (options.HasUseHybridSearch) result.UseHybridSearch = options.UseHybridSearch;
        if (options.HasUseLostInTheMiddleReordering) result.UseLostInTheMiddleReordering = options.UseLostInTheMiddleReordering;
        if (options.HasUseRedundancyFilter) result.UseRedundancyFilter = options.UseRedundancyFilter;
        if (options.HasRedundancyThreshold) result.RedundancyThreshold = options.RedundancyThreshold;
        if (options.MetadataFilter.Count > 0) result.MetadataFilter = ToMetadataFilter(options.MetadataFilter);

        return result;
    }

    /// <summary>The <see cref="RetrievalOptions"/> for a request; <see langword="null"/> when it sent none.</summary>
    /// <param name="options">The request's retrieval options.</param>
    internal static RetrievalOptions? ToRetrievalOptions(RetrievalOptionsProto? options)
    {
        if (options is null)
            return null;

        var d = RetrievalDefaults;
        return new RetrievalOptions
        {
            TopK = options.HasTopK ? options.TopK : d.TopK,
            MinScore = options.HasMinScore ? options.MinScore : d.MinScore,
            MetadataFilter = options.MetadataFilter.Count > 0 ? ToMetadataFilter(options.MetadataFilter) : d.MetadataFilter,
            UseHybridSearch = options.HasUseHybridSearch ? options.UseHybridSearch : d.UseHybridSearch,
            EnsembleOptions = ToEnsembleOptions(options.EnsembleOptions),
            UseSparseSearch = options.HasUseSparseSearch ? options.UseSparseSearch : d.UseSparseSearch,
            UseLostInTheMiddleReordering = options.HasUseLostInTheMiddleReordering ? options.UseLostInTheMiddleReordering : d.UseLostInTheMiddleReordering,
            UseRedundancyFilter = options.HasUseRedundancyFilter ? options.UseRedundancyFilter : d.UseRedundancyFilter,
            RedundancyThreshold = options.HasRedundancyThreshold ? options.RedundancyThreshold : d.RedundancyThreshold,
            MaxContextTokens = options.HasMaxContextTokens ? options.MaxContextTokens : d.MaxContextTokens,
            UseMmr = options.HasUseMmr ? options.UseMmr : d.UseMmr,
            MmrLambda = options.HasMmrLambda ? options.MmrLambda : d.MmrLambda,
            MmrCandidateCount = options.HasMmrCandidateCount ? options.MmrCandidateCount : d.MmrCandidateCount,
            UseMultiQuery = options.HasUseMultiQuery ? options.UseMultiQuery : d.UseMultiQuery,
            UseReranking = options.HasUseReranking ? options.UseReranking : d.UseReranking,
            CandidateCount = options.HasCandidateCount ? options.CandidateCount : d.CandidateCount,
            UseHyde = options.HasUseHyde ? options.UseHyde : d.UseHyde,
            UseAdaptiveRetrieval = options.HasUseAdaptiveRetrieval ? options.UseAdaptiveRetrieval : d.UseAdaptiveRetrieval,
            UseCrag = options.HasUseCrag ? options.UseCrag : d.UseCrag,
            CragScoreThreshold = options.HasCragScoreThreshold ? options.CragScoreThreshold : d.CragScoreThreshold,
            CragFallbackMode = options.HasCragFallbackMode ? ToCragFallbackMode(options.CragFallbackMode) ?? d.CragFallbackMode : d.CragFallbackMode,
            UseSelfQuery = options.HasUseSelfQuery ? options.UseSelfQuery : d.UseSelfQuery,
            UseCacheEmbedding = options.HasUseCacheEmbedding ? options.UseCacheEmbedding : d.UseCacheEmbedding,
            UseCacheResult = options.HasUseCacheResult ? options.UseCacheResult : d.UseCacheResult,
            UseParentDocument = options.HasUseParentDocument ? options.UseParentDocument : d.UseParentDocument,
            UseTagRetrieval = options.HasUseTagRetrieval ? options.UseTagRetrieval : d.UseTagRetrieval,
            UseTimeWeighting = options.HasUseTimeWeighting ? options.UseTimeWeighting : d.UseTimeWeighting,
        };
    }

    private static EnsembleOptions? ToEnsembleOptions(EnsembleOptionsProto? options) => options is null
        ? null
        : new EnsembleOptions
        {
            DenseWeight = options.HasDenseWeight ? options.DenseWeight : EnsembleDefaults.DenseWeight,
            Bm25Weight = options.HasBm25Weight ? options.Bm25Weight : EnsembleDefaults.Bm25Weight,
            SparseWeight = options.HasSparseWeight ? options.SparseWeight : EnsembleDefaults.SparseWeight,
            K = options.HasK ? options.K : EnsembleDefaults.K,
        };

    private static MapReduceOptions? ToMapReduceOptions(MapReduceOptionsProto? options) => options is null
        ? null
        : new MapReduceOptions
        {
            MapConcurrency = options.HasMapConcurrency ? options.MapConcurrency : MapReduceDefaults.MapConcurrency,
            MapPromptTemplate = options.HasMapPromptTemplate ? options.MapPromptTemplate : null,
            ReducePromptTemplate = options.HasReducePromptTemplate ? options.ReducePromptTemplate : null,
        };

    // UNSPECIFIED is proto3's zero value; it means "not set", so the default applies.
    private static SynthesisStrategy? ToSynthesisStrategy(SynthesisStrategyProto value) => value switch
    {
        SynthesisStrategyProto.SynthesisStrategyDefault => SynthesisStrategy.Default,
        SynthesisStrategyProto.SynthesisStrategyMapReduce => SynthesisStrategy.MapReduce,
        SynthesisStrategyProto.SynthesisStrategyRefine => SynthesisStrategy.Refine,
        SynthesisStrategyProto.SynthesisStrategyFlare => SynthesisStrategy.Flare,
        _ => null,
    };

    private static CragFallbackMode? ToCragFallbackMode(CragFallbackModeProto value) => value switch
    {
        CragFallbackModeProto.CragFallbackModeReplace => CragFallbackMode.Replace,
        CragFallbackModeProto.CragFallbackModeAppend => CragFallbackMode.Append,
        _ => null,
    };

    private static Dictionary<string, MetadataValue> ToMetadataFilter(
        Google.Protobuf.Collections.MapField<string, MetadataValueProto> filter)
    {
        var result = new Dictionary<string, MetadataValue>(filter.Count, StringComparer.Ordinal);
        foreach (var (key, value) in filter)
        {
            result[key] = value.KindCase switch
            {
                MetadataValueProto.KindOneofCase.NumberValue => value.NumberValue,
                MetadataValueProto.KindOneofCase.BooleanValue => value.BooleanValue,
                MetadataValueProto.KindOneofCase.DateTimeOffsetValue => value.DateTimeOffsetValue.ToDateTimeOffset(),
                _ => value.StringValue,
            };
        }

        return result;
    }

    private static string? RejectValues(
        Google.Protobuf.Collections.MapField<string, MetadataValueProto> filter, string path)
    {
        foreach (var (key, value) in filter)
        {
            if (value is null || value.KindCase == MetadataValueProto.KindOneofCase.None)
                return $"{path}[\"{key}\"] has no value.";
        }

        return null;
    }

    // The fields that replace or steer the prompt the server's answer engine sends: the three
    // #944 names, and the map-reduce and refine templates, which replace prompt text outright.
    private static List<string> PromptShapingFields(RagOptionsProto options)
    {
        var fields = new List<string>();
        if (options.HasSystemPrompt) fields.Add("options.system_prompt");
        if (options.HasTemperature) fields.Add("options.temperature");
        if (options.ConversationHistory.Count > 0) fields.Add("options.conversation_history");
        if (options.MapReduceOptions?.HasMapPromptTemplate == true) fields.Add("options.map_reduce_options.map_prompt_template");
        if (options.MapReduceOptions?.HasReducePromptTemplate == true) fields.Add("options.map_reduce_options.reduce_prompt_template");
        if (options.RefineOptions?.HasInitialPromptTemplate == true) fields.Add("options.refine_options.initial_prompt_template");
        if (options.RefineOptions?.HasRefinePromptTemplate == true) fields.Add("options.refine_options.refine_prompt_template");
        return fields;
    }

    private static List<string> ShortFormFields(RagOptionsProto options)
    {
        var fields = new List<string>();
        if (options.HasTopK) fields.Add("options.top_k");
        if (options.HasMaxContextTokens) fields.Add("options.max_context_tokens");
        if (options.HasMinScore) fields.Add("options.min_score");
        if (options.HasUseHybridSearch) fields.Add("options.use_hybrid_search");
        if (options.HasUseLostInTheMiddleReordering) fields.Add("options.use_lost_in_the_middle_reordering");
        if (options.HasUseRedundancyFilter) fields.Add("options.use_redundancy_filter");
        if (options.HasRedundancyThreshold) fields.Add("options.redundancy_threshold");
        if (options.MetadataFilter.Count > 0) fields.Add("options.metadata_filter");
        return fields;
    }
}
