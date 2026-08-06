using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Telemetry;

namespace Rag.NET.Reranking.Onnx;

public sealed class OnnxReranker : IReranker, IDisposable
{
    private readonly InferenceSession _session;
    private readonly OnnxRerankerOptions _options;
    private readonly CrossEncoderPairTokenizer _tokenizer;

    public OnnxReranker(OnnxRerankerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Before the file checks, so the message names the option rather than a missing file.
        if (options.MaxLength <= CrossEncoderPairTokenizer.SpecialTokensPerPair)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"MaxLength ({options.MaxLength}) must exceed the " +
                $"{CrossEncoderPairTokenizer.SpecialTokensPerPair} positions reserved for [CLS] and the two [SEP].");
        }

        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"ONNX model file not found: {options.ModelPath}", options.ModelPath);

        if (!File.Exists(options.VocabPath))
            throw new FileNotFoundException(
                $"BERT vocabulary file not found: {options.VocabPath}", options.VocabPath);

        _options = options;
        _tokenizer = new CrossEncoderPairTokenizer(options.VocabPath);
        _session = new InferenceSession(options.ModelPath);
    }

    public Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(results);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.rerank");
        activity?.SetTag("reranker.type", nameof(OnnxReranker));
        activity?.SetTag("reranker.candidate.count", results.Count);

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
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Encode(query, passage, _options.MaxLength);
        ReadOnlySpan<int> shape = [1, inputIds.Length];

        return (
            new DenseTensor<long>(inputIds, shape),
            new DenseTensor<long>(attentionMask, shape),
            new DenseTensor<long>(tokenTypeIds, shape));
    }

    private static double Sigmoid(float x) => 1.0 / (1.0 + Math.Exp(-x));

    public void Dispose() => _session.Dispose();
}
