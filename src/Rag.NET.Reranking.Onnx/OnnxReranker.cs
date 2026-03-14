using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Reranking.Onnx;

public sealed class OnnxReranker : IReranker, IDisposable
{
    private readonly InferenceSession _session;
    private readonly OnnxRerankerOptions _options;

    public OnnxReranker(OnnxRerankerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"ONNX model file not found: {options.ModelPath}", options.ModelPath);

        _options = options;
        _session = new InferenceSession(options.ModelPath);
    }

    public Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
            return Task.FromResult<IReadOnlyList<RerankResult>>([]);

        var rerankResults = new List<RerankResult>(results.Count);

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var score = ScorePair(query, result.Chunk.Text);
            rerankResults.Add(new RerankResult
            {
                SearchResult = result,
                RelevanceScore = score,
            });
        }

        return Task.FromResult<IReadOnlyList<RerankResult>>(rerankResults);
    }

    private double ScorePair(string query, string passage)
    {
        var (inputIds, attentionMask, tokenTypeIds) = TokenizePair(query, passage);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var outputs = _session.Run(inputs);
        var logits = outputs.First().AsTensor<float>();
        return Sigmoid(logits[0]);
    }

    private (DenseTensor<long> InputIds, DenseTensor<long> AttentionMask, DenseTensor<long> TokenTypeIds) TokenizePair(
        string query, string passage)
    {
        var maxLen = _options.MaxLength;
        var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var passageTokens = passage.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Reserve 3 tokens for [CLS] and two [SEP]
        var available = maxLen - 3;
        var queryLen = Math.Min(queryTokens.Length, available / 2);
        var passageLen = Math.Min(passageTokens.Length, available - queryLen);
        var totalLen = queryLen + passageLen + 3;

        var inputIds = new DenseTensor<long>([1, totalLen]);
        var attentionMask = new DenseTensor<long>([1, totalLen]);
        var tokenTypeIds = new DenseTensor<long>([1, totalLen]);

        // [CLS] = 101
        inputIds[0, 0] = 101;
        attentionMask[0, 0] = 1;
        tokenTypeIds[0, 0] = 0;

        var pos = 1;
        for (var i = 0; i < queryLen; i++, pos++)
        {
            inputIds[0, pos] = (uint)(queryTokens[i].GetHashCode(StringComparison.Ordinal) & 0x7FFF);
            attentionMask[0, pos] = 1;
            tokenTypeIds[0, pos] = 0;
        }

        // [SEP] = 102
        inputIds[0, pos] = 102;
        attentionMask[0, pos] = 1;
        tokenTypeIds[0, pos] = 0;
        pos++;

        for (var i = 0; i < passageLen; i++, pos++)
        {
            inputIds[0, pos] = (uint)(passageTokens[i].GetHashCode(StringComparison.Ordinal) & 0x7FFF);
            attentionMask[0, pos] = 1;
            tokenTypeIds[0, pos] = 1;
        }

        // [SEP]
        inputIds[0, pos] = 102;
        attentionMask[0, pos] = 1;
        tokenTypeIds[0, pos] = 1;

        return (inputIds, attentionMask, tokenTypeIds);
    }

    private static double Sigmoid(float x) => 1.0 / (1.0 + Math.Exp(-x));

    public void Dispose() => _session.Dispose();
}
