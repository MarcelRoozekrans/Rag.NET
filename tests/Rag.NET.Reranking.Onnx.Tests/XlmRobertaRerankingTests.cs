using Rag.NET.Models;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.Reranking.Onnx.Tests;

/// <summary>
/// Real-model check that <see cref="OnnxReranker"/> with a SentencePiece tokenizer ranks Dutch
/// passages sensibly. The model is the community ONNX export
/// <c>EmbeddedLLM/bge-reranker-v2-m3-onnx-o3-cpu</c> at commit
/// <c>c46cc14e4748b2332899dfe5e6dcd4750caf7cef</c> (inputs <c>input_ids</c> and
/// <c>attention_mask</c>, one <c>logits</c> value per pair), and the tokenizer is
/// <c>sentencepiece.bpe.model</c> of <c>BAAI/bge-reranker-v2-m3</c> at commit
/// <c>953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e</c>. Skipped unless
/// <c>RAGNET_ONNX_RERANK_XLMR_MODEL</c> and <c>RAGNET_ONNX_RERANK_TOKENIZER</c> name existing files,
/// because neither model is committed.
/// </summary>
public sealed class XlmRobertaRerankingTests
{
    public static TheoryData<string, string, string[]> Cases => new()
    {
        {
            "Wat is de hoofdstad van Nederland?",
            "Amsterdam is de hoofdstad van Nederland, hoewel de regering en het parlement in Den Haag zetelen.",
            [
                "Nederland heeft ruim zeventien miljoen inwoners en is een van de dichtstbevolkte landen van Europa.",
                "De hoofdstad van Frankrijk is Parijs, dat aan de Seine ligt.",
                "In Nederland is het Binnenhof het bekendste gebouwencomplex, en het land staat bekend om zijn tulpen.",
            ]
        },
        {
            "Hoeveel leden heeft de Tweede Kamer?",
            "De Tweede Kamer bestaat uit 150 leden, die elke vier jaar bij de Tweede Kamerverkiezingen worden gekozen.",
            [
                "De Tweede Kamer vergadert in Den Haag, in een gebouw aan het Binnenhof dat sinds 1992 in gebruik is.",
                "De Eerste Kamer wordt niet rechtstreeks gekozen, maar door de leden van de Provinciale Staten.",
                "Veel leden van sportclubs in Nederland betalen contributie aan het begin van het seizoen.",
            ]
        },
        {
            "Vanaf welke leeftijd mag je een rijbewijs voor de auto aanvragen?",
            "In Nederland kun je vanaf 17 jaar een rijbewijs voor de auto halen, en vanaf 18 jaar mag je ook zonder begeleider rijden.",
            [
                "Een rijbewijs voor de auto is tien jaar geldig en moet daarna worden verlengd bij de gemeente.",
                "Wie een rijbewijs aanvraagt, betaalt daarvoor leges aan de gemeente en moet een recente pasfoto meenemen.",
                "Je kunt al op 16 jaar een bromfietsrijbewijs halen om een brommer te mogen besturen.",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RerankAsync_RanksTheAnsweringDutchPassageFirst(
        string question, string answer, string[] distractors)
    {
        var modelPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_RERANK_XLMR_MODEL");
        var tokenizerPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_RERANK_TOKENIZER");
        Assert.SkipWhen(
            string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath) ||
            string.IsNullOrEmpty(tokenizerPath) || !File.Exists(tokenizerPath),
            "Set RAGNET_ONNX_RERANK_XLMR_MODEL and RAGNET_ONNX_RERANK_TOKENIZER to existing "
            + "bge-reranker-v2-m3 model and sentencepiece.bpe.model files to run this test."
            + BeirProvisioningHint.Describe());

        using var reranker = new OnnxReranker(new OnnxRerankerOptions
        {
            ModelPath = modelPath!,
            TokenizerPath = tokenizerPath!,
        });

        string[] passages = [.. distractors.Take(1), answer, .. distractors.Skip(1)];
        var candidates = passages
            .Select((text, i) => new SearchResult
            {
                Chunk = new TextChunk { Text = text, DocumentId = new DocumentId("doc-" + i), ChunkIndex = 0 },
                Score = 0.5,
            })
            .ToList();

        var results = await reranker.RerankAsync(question, candidates, TestContext.Current.CancellationToken);

        var scores = string.Join("; ", results.Select(r => $"{r.RelevanceScore:F3}: {r.SearchResult.Chunk.Text[..30]}"));
        var best = results.MaxBy(r => r.RelevanceScore)!;
        Assert.True(string.Equals(best.SearchResult.Chunk.Text, answer, StringComparison.Ordinal), $"Expected the answering passage first. Scores: {scores}");
    }
}
