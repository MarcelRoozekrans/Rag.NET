using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Telemetry;

namespace Rag.NET.Reranking.Onnx;

public sealed class OnnxReranker : IReranker, IDisposable
{
    private static readonly string[] SuppliedInputs = ["input_ids", "attention_mask", "token_type_ids"];

    private readonly InferenceSession _session;
    private readonly OnnxRerankerOptions _options;
    private readonly IPairTokenizer _tokenizer;
    private readonly bool _feedsAttentionMask;
    private readonly bool _feedsTokenTypeIds;

    public OnnxReranker(OnnxRerankerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var useTokenizer = ValidateOptions(options);

        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"ONNX model file not found: {options.ModelPath}", options.ModelPath);

        _tokenizer = useTokenizer
            ? LoadSentencePieceTokenizer(options.TokenizerPath!)
            : LoadBertTokenizer(options.VocabPath);

        _options = options;
        _session = new InferenceSession(options.ModelPath);

        // Feed only what the export declares: XLM-RoBERTa exports usually take no token_type_ids, and
        // ONNX Runtime rejects an input the graph does not have.
        var declared = _session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
        var unsupported = declared
            .Where(name => Array.IndexOf(SuppliedInputs, name) < 0)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unsupported.Length > 0 || !declared.Contains("input_ids"))
        {
            var problem = unsupported.Length > 0
                ? $"declares {(unsupported.Length == 1 ? "input" : "inputs")} " +
                  string.Join(", ", unsupported.Select(name => $"'{name}'"))
                : "does not declare input 'input_ids'";
            _session.Dispose();
            throw new InvalidOperationException(
                $"The ONNX model {problem}, but the reranker supplies only " +
                $"{string.Join(", ", SuppliedInputs)}.");
        }

        _feedsAttentionMask = declared.Contains("attention_mask");
        _feedsTokenTypeIds = declared.Contains("token_type_ids");
    }

    /// <returns>True when <see cref="OnnxRerankerOptions.TokenizerPath"/> selects the SentencePiece tokenizer.</returns>
    private static bool ValidateOptions(OnnxRerankerOptions options)
    {
        var useTokenizer = !string.IsNullOrWhiteSpace(options.TokenizerPath);
        var useVocab = !string.IsNullOrWhiteSpace(options.VocabPath);
        if (useTokenizer == useVocab)
        {
            throw new ArgumentException(
                "Set exactly one of VocabPath (BERT vocab.txt) and TokenizerPath (SentencePiece model); " +
                (useTokenizer ? "both are set." : "neither is set."),
                nameof(options));
        }

        // Before the file checks, so the message names the option rather than a missing file.
        var specialTokens = useTokenizer
            ? XlmRobertaPairTokenizer.SpecialTokensPerPair
            : CrossEncoderPairTokenizer.SpecialTokensPerPair;
        if (options.MaxLength <= specialTokens)
        {
            var reserved = useTokenizer
                ? "<s>, the two </s> between the segments and the closing </s>"
                : "[CLS] and the two [SEP]";
            throw new ArgumentOutOfRangeException(nameof(options),
                $"MaxLength ({options.MaxLength}) must exceed the {specialTokens} positions reserved for {reserved}.");
        }

        return useTokenizer;
    }

    private static CrossEncoderPairTokenizer LoadBertTokenizer(string vocabPath)
    {
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"BERT vocabulary file not found: {vocabPath}", vocabPath);

        return new CrossEncoderPairTokenizer(vocabPath);
    }

    private static XlmRobertaPairTokenizer LoadSentencePieceTokenizer(string tokenizerPath)
    {
        if (!File.Exists(tokenizerPath))
            throw new FileNotFoundException($"SentencePiece model file not found: {tokenizerPath}", tokenizerPath);

        try
        {
            return new XlmRobertaPairTokenizer(tokenizerPath);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"TokenizerPath '{tokenizerPath}' is not a SentencePiece model file (sentencepiece.bpe.model).", ex);
        }
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

        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
        };
        if (_feedsAttentionMask)
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask));
        if (_feedsTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));

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
