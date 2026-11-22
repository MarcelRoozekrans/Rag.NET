using Rag.NET.Api.Contracts;
using Rag.NET.Models.Options;
using Rag.NET.Remote;

namespace Rag.NET.Api.Client;

/// <summary>
/// Turns the caller's options into the wire options the server maps back (#944). Every value the
/// caller holds is sent, except the short-form retrieval fields it did not assign: the server
/// then applies the same defaults the in-process <c>RagPipeline</c> does.
/// </summary>
internal static class RagOptionsWire
{
    private const string Client = nameof(HttpRagPipeline);

    internal static RagOptionsDto? ToDto(RagOptions? options)
    {
        if (options is null)
            return null;

        var assigned = RemoteOptionsGuard.CheckSendable(options, Client);
        bool Sent(string field) => assigned.Contains(field, StringComparer.Ordinal);

        return new RagOptionsDto
        {
            TopK = Sent(nameof(RagOptions.TopK)) ? options.TopK : null,
            MaxContextTokens = Sent(nameof(RagOptions.MaxContextTokens)) ? options.MaxContextTokens : null,
            MinScore = Sent(nameof(RagOptions.MinScore)) ? options.MinScore : null,
            UseHybridSearch = Sent(nameof(RagOptions.UseHybridSearch)) ? options.UseHybridSearch : null,
            UseLostInTheMiddleReordering = Sent(nameof(RagOptions.UseLostInTheMiddleReordering)) ? options.UseLostInTheMiddleReordering : null,
            UseRedundancyFilter = Sent(nameof(RagOptions.UseRedundancyFilter)) ? options.UseRedundancyFilter : null,
            RedundancyThreshold = Sent(nameof(RagOptions.RedundancyThreshold)) ? options.RedundancyThreshold : null,
            MetadataFilter = Sent(nameof(RagOptions.MetadataFilter)) ? options.MetadataFilter : null,
            Retrieval = ToDto(options.Retrieval),
            SystemPrompt = options.SystemPrompt,
            SendSourcesAsToolResult = options.SendSourcesAsToolResult,
            Temperature = options.Temperature,
            ConversationHistory = options.ConversationHistory is { Count: > 0 } history
                ? history.Select(m => new ChatMessageDto(m.Role.Value, m.Text)).ToList()
                : null,
            SynthesisStrategy = options.SynthesisStrategy,
            MapReduceOptions = options.MapReduceOptions is { } mapReduce
                ? new MapReduceOptionsDto
                {
                    MapConcurrency = mapReduce.MapConcurrency,
                    MapPromptTemplate = mapReduce.MapPromptTemplate,
                    ReducePromptTemplate = mapReduce.ReducePromptTemplate,
                }
                : null,
            RefineOptions = options.RefineOptions is { } refine
                ? new RefineOptionsDto
                {
                    InitialPromptTemplate = refine.InitialPromptTemplate,
                    RefinePromptTemplate = refine.RefinePromptTemplate,
                }
                : null,
            SkipCompression = options.SkipCompression,
        };
    }

    internal static RetrievalOptionsDto? ToDto(RetrievalOptions? options)
    {
        if (options is null)
            return null;

        RemoteOptionsGuard.CheckSendable(options, Client);
        return new RetrievalOptionsDto
        {
            TopK = options.TopK,
            MinScore = options.MinScore,
            MetadataFilter = options.MetadataFilter,
            UseHybridSearch = options.UseHybridSearch,
            EnsembleOptions = options.EnsembleOptions is { } ensemble
                ? new EnsembleOptionsDto
                {
                    DenseWeight = ensemble.DenseWeight,
                    Bm25Weight = ensemble.Bm25Weight,
                    SparseWeight = ensemble.SparseWeight,
                    K = ensemble.K,
                }
                : null,
            UseSparseSearch = options.UseSparseSearch,
            UseLostInTheMiddleReordering = options.UseLostInTheMiddleReordering,
            UseRedundancyFilter = options.UseRedundancyFilter,
            RedundancyThreshold = options.RedundancyThreshold,
            MaxContextTokens = options.MaxContextTokens,
            UseMmr = options.UseMmr,
            MmrLambda = options.MmrLambda,
            MmrCandidateCount = options.MmrCandidateCount,
            UseMultiQuery = options.UseMultiQuery,
            UseReranking = options.UseReranking,
            CandidateCount = options.CandidateCount,
            UseHyde = options.UseHyde,
            UseAdaptiveRetrieval = options.UseAdaptiveRetrieval,
            UseCrag = options.UseCrag,
            CragScoreThreshold = options.CragScoreThreshold,
            CragFallbackMode = options.CragFallbackMode,
            UseSelfQuery = options.UseSelfQuery,
            UseCacheEmbedding = options.UseCacheEmbedding,
            UseCacheResult = options.UseCacheResult,
            UseParentDocument = options.UseParentDocument,
            UseTagRetrieval = options.UseTagRetrieval,
            UseTimeWeighting = options.UseTimeWeighting,
        };
    }
}
