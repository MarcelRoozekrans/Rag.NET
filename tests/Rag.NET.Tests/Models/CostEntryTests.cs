using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

/// <summary>
/// Pins the shape that lets <c>ICostLedger</c> represent a per-page API: an OCR entry states
/// pages and zero tokens rather than fabricating a token count, and the token counters became
/// optional without breaking the initialisers that already set them.
/// </summary>
public class CostEntryTests
{
    [Fact]
    public void CostEntry_OcrEntry_CarriesPagesAndZeroTokens()
    {
        // No InputTokens/OutputTokens here: Document Intelligence reports pages, not tokens,
        // and inventing a token count is exactly what this shape exists to avoid.
        var sut = new CostEntry
        {
            Kind = CostKind.Ocr,
            Pages = 12,
            Cost = 0.018m,
        };

        Assert.Equal(CostKind.Ocr, sut.Kind);
        Assert.Equal(12, sut.Pages);
        Assert.Equal(0L, sut.InputTokens);
        Assert.Equal(0L, sut.OutputTokens);
        Assert.Equal(0.018m, sut.Cost);
    }

    [Fact]
    public void CostEntry_ExistingTokenEntries_StillCompileAndRoundTrip()
    {
        // Verbatim the initialiser shape that predates Pages — that this compiles is the
        // guard on dropping `required` from the token counters.
        var chat = new CostEntry
        {
            Kind = CostKind.Chat,
            InputTokens = 100,
            OutputTokens = 50,
            Cost = 0.25m,
        };

        var embedding = new CostEntry
        {
            Kind = CostKind.Embedding,
            InputTokens = 500,
            OutputTokens = 0,
            Cost = 0.20m,
        };

        Assert.Equal(100L, chat.InputTokens);
        Assert.Equal(50L, chat.OutputTokens);
        Assert.Equal(0.25m, chat.Cost);
        Assert.Equal(0, chat.Pages); // token entries never set Pages

        Assert.Equal(500L, embedding.InputTokens);
        Assert.Equal(0L, embedding.OutputTokens);
        Assert.Equal(0, embedding.Pages);
    }

    [Fact]
    public void CostEntry_UnitCountersDefaultToZero()
    {
        // Kind and Cost are still required — that is enforced by the compiler, not assertable
        // here. What this pins is the consequence of the counters no longer being: omitting
        // them is legal and yields zero, rather than some sentinel.
        var sut = new CostEntry { Kind = CostKind.Chat, Cost = 1m };

        Assert.Equal(0L, sut.InputTokens);
        Assert.Equal(0L, sut.OutputTokens);
        Assert.Equal(0, sut.Pages);
    }
}
