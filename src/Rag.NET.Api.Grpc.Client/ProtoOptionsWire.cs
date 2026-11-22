using Google.Protobuf.WellKnownTypes;
using Rag.NET.Api.Grpc.Proto;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Remote;

namespace Rag.NET.Api.Grpc.Client;

/// <summary>
/// Turns the caller's options into the proto options the server maps back (#944). Every value the
/// caller holds is sent, except the short-form retrieval fields it did not assign: the server then
/// applies the same defaults the in-process <c>RagPipeline</c> does.
/// </summary>
internal static class ProtoOptionsWire
{
    private const string Client = nameof(GrpcRagPipeline);

    internal static RagOptionsProto? ToProto(RagOptions? options)
    {
        if (options is null)
            return null;

        var assigned = RemoteOptionsGuard.CheckSendable(options, Client);
        bool Sent(string field) => assigned.Contains(field, StringComparer.Ordinal);

        var proto = new RagOptionsProto
        {
            Retrieval = ToProto(options.Retrieval),
            SendSourcesAsToolResult = options.SendSourcesAsToolResult,
            SynthesisStrategy = ToProto(options.SynthesisStrategy),
            MapReduceOptions = ToProto(options.MapReduceOptions),
            RefineOptions = ToProto(options.RefineOptions),
            SkipCompression = options.SkipCompression,
        };

        if (Sent(nameof(RagOptions.TopK))) proto.TopK = options.TopK;
        if (Sent(nameof(RagOptions.MaxContextTokens)) && options.MaxContextTokens is { } maxTokens) proto.MaxContextTokens = maxTokens;
        if (Sent(nameof(RagOptions.MinScore))) proto.MinScore = options.MinScore;
        if (Sent(nameof(RagOptions.UseHybridSearch))) proto.UseHybridSearch = options.UseHybridSearch;
        if (Sent(nameof(RagOptions.UseLostInTheMiddleReordering))) proto.UseLostInTheMiddleReordering = options.UseLostInTheMiddleReordering;
        if (Sent(nameof(RagOptions.UseRedundancyFilter))) proto.UseRedundancyFilter = options.UseRedundancyFilter;
        if (Sent(nameof(RagOptions.RedundancyThreshold))) proto.RedundancyThreshold = options.RedundancyThreshold;
        if (Sent(nameof(RagOptions.MetadataFilter))) AddFilter(proto.MetadataFilter, options.MetadataFilter);

        if (options.SystemPrompt is not null) proto.SystemPrompt = options.SystemPrompt;
        if (options.Temperature is { } temperature) proto.Temperature = temperature;
        if (options.ConversationHistory is { } history)
        {
            foreach (var message in history)
                proto.ConversationHistory.Add(new ChatMessageProto { Role = message.Role.Value, Text = message.Text });
        }

        return proto;
    }

    internal static RetrievalOptionsProto? ToProto(RetrievalOptions? options)
    {
        if (options is null)
            return null;

        RemoteOptionsGuard.CheckSendable(options, Client);
        var proto = new RetrievalOptionsProto
        {
            TopK = options.TopK,
            MinScore = options.MinScore,
            UseHybridSearch = options.UseHybridSearch,
            EnsembleOptions = ToProto(options.EnsembleOptions),
            UseLostInTheMiddleReordering = options.UseLostInTheMiddleReordering,
            UseRedundancyFilter = options.UseRedundancyFilter,
            RedundancyThreshold = options.RedundancyThreshold,
            UseMmr = options.UseMmr,
            MmrLambda = options.MmrLambda,
            UseMultiQuery = options.UseMultiQuery,
            UseReranking = options.UseReranking,
            UseHyde = options.UseHyde,
            UseAdaptiveRetrieval = options.UseAdaptiveRetrieval,
            UseCrag = options.UseCrag,
            CragScoreThreshold = options.CragScoreThreshold,
            CragFallbackMode = ToProto(options.CragFallbackMode),
            UseSelfQuery = options.UseSelfQuery,
            UseCacheEmbedding = options.UseCacheEmbedding,
            UseCacheResult = options.UseCacheResult,
            UseParentDocument = options.UseParentDocument,
            UseTagRetrieval = options.UseTagRetrieval,
            UseTimeWeighting = options.UseTimeWeighting,
        };

        if (options.UseSparseSearch is { } useSparse) proto.UseSparseSearch = useSparse;
        if (options.MaxContextTokens is { } maxTokens) proto.MaxContextTokens = maxTokens;
        if (options.MmrCandidateCount is { } mmrCandidates) proto.MmrCandidateCount = mmrCandidates;
        if (options.CandidateCount is { } candidates) proto.CandidateCount = candidates;
        AddFilter(proto.MetadataFilter, options.MetadataFilter);
        return proto;
    }

    private static EnsembleOptionsProto? ToProto(EnsembleOptions? options) => options is null
        ? null
        : new EnsembleOptionsProto
        {
            DenseWeight = options.DenseWeight,
            Bm25Weight = options.Bm25Weight,
            SparseWeight = options.SparseWeight,
            K = options.K,
        };

    private static MapReduceOptionsProto? ToProto(MapReduceOptions? options)
    {
        if (options is null)
            return null;

        var proto = new MapReduceOptionsProto { MapConcurrency = options.MapConcurrency };
        if (options.MapPromptTemplate is not null) proto.MapPromptTemplate = options.MapPromptTemplate;
        if (options.ReducePromptTemplate is not null) proto.ReducePromptTemplate = options.ReducePromptTemplate;
        return proto;
    }

    private static RefineOptionsProto? ToProto(RefineOptions? options)
    {
        if (options is null)
            return null;

        var proto = new RefineOptionsProto();
        if (options.InitialPromptTemplate is not null) proto.InitialPromptTemplate = options.InitialPromptTemplate;
        if (options.RefinePromptTemplate is not null) proto.RefinePromptTemplate = options.RefinePromptTemplate;
        return proto;
    }

    private static SynthesisStrategyProto ToProto(SynthesisStrategy strategy) => strategy switch
    {
        SynthesisStrategy.Default => SynthesisStrategyProto.SynthesisStrategyDefault,
        SynthesisStrategy.MapReduce => SynthesisStrategyProto.SynthesisStrategyMapReduce,
        SynthesisStrategy.Refine => SynthesisStrategyProto.SynthesisStrategyRefine,
        SynthesisStrategy.Flare => SynthesisStrategyProto.SynthesisStrategyFlare,
        _ => throw new NotSupportedException($"{Client} cannot send SynthesisStrategy {strategy}."),
    };

    private static CragFallbackModeProto ToProto(CragFallbackMode mode) => mode switch
    {
        CragFallbackMode.Replace => CragFallbackModeProto.CragFallbackModeReplace,
        CragFallbackMode.Append => CragFallbackModeProto.CragFallbackModeAppend,
        _ => throw new NotSupportedException($"{Client} cannot send CragFallbackMode {mode}."),
    };

    private static void AddFilter(
        Google.Protobuf.Collections.MapField<string, MetadataValueProto> target,
        IDictionary<string, MetadataValue>? filter)
    {
        if (filter is null)
            return;

        foreach (var (key, value) in filter)
        {
            target[key] = value.Kind switch
            {
                MetadataValueKind.Number => new MetadataValueProto { NumberValue = value.NumberValue },
                MetadataValueKind.Boolean => new MetadataValueProto { BooleanValue = value.BooleanValue },
                MetadataValueKind.DateTimeOffset => new MetadataValueProto { DateTimeOffsetValue = Timestamp.FromDateTimeOffset(value.DateTimeOffsetValue) },
                _ => new MetadataValueProto { StringValue = value.StringValue },
            };
        }
    }
}
