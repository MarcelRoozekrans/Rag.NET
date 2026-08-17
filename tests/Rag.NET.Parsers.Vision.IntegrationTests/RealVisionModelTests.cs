using Microsoft.Extensions.AI;
using Rag.NET.Models;
using Rag.NET.Parsers.Vision;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.Parsers.Vision.IntegrationTests;

/// <summary>
/// Drives <see cref="ImageDocumentParser"/> against a real vision model over a real image — the
/// first time anything in this repository has sent an image to one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Milestone 6, Phase 6.1.</b> Every test in <c>Rag.NET.Parsers.Vision.Tests</c> either
/// substitutes <see cref="IChatClient"/> or subclasses the parser and overrides
/// <c>DescribeImageAsync</c> (<c>FakeImageDocumentParser</c>, <c>FakeOcrParser</c>). Those cover the
/// code around the call — content-type routing, the OCR-first branch, sanitisation, section shape —
/// and none of them ever built a multimodal message or learned whether a model can read what we
/// send it.
/// </para>
/// <para>
/// <b>What this actually proves that a substitute cannot.</b> The parser packs the image as a
/// <see cref="DataContent"/> and the prompt as a <see cref="TextContent"/> in one
/// <see cref="ChatMessage"/>. A substitute returns whatever it was told to regardless of whether
/// that message is well-formed, so the wire shape has never been checked against a real endpoint.
/// If the ordering, the media type, or the base64 encoding were wrong, every existing test would
/// still pass.
/// </para>
/// <para>
/// <b>Provenance of <c>Resources/sample-image.png</c>:</b> drawn 2026-08-17 by <b>GDI+</b>
/// (<c>System.Drawing</c>) — a solid red circle and the words "INTEGRATION" and "TEST" in 64pt bold
/// Arial on white, 974x392, 13,398 bytes. The canvas is sized from <c>MeasureString</c> rather than
/// a guessed constant, because the first attempt clipped the final "N" off "INTEGRATION" and would
/// have made this suite assert on a word that is not in the image. The producer is deliberately
/// unrelated to the consumer, the same reasoning as the Office, ZIP and WAV fixtures.
/// </para>
/// <para>
/// <b>The assertions were shown to discriminate, not just to pass.</b> An assertion on a model's
/// prose is worth very little until something establishes it can fail. A probe sent a blank white
/// PNG of the same dimensions to the same model on 2026-08-17: it answered "a completely blank,
/// white rectangle ... there are no shapes", and both content assertions below — the word
/// "INTEGRATION" and the colour "red" — evaluate false against that reply. The probe is not
/// committed as a test because it would double the cost of every run to re-establish a fact about
/// the assertions rather than about the code.
/// </para>
/// <para>
/// <b>Cost, since these tests spend real money.</b> Two calls per run at roughly $0.0002 each on
/// <c>qwen/qwen3.7-flash</c> — measured, not estimated: the probe that chose this model reported
/// $0.00018527 for one call. Override with <c>OPENROUTER_VISION_MODEL</c>.
/// </para>
/// <para>
/// <b>Gated on the key, with no local fallback.</b> <c>TestChatClientFactory.Create</c> falls back
/// to a 1B Ollama text model, which cannot see. The smallest usable local vision model is a
/// multi-gigabyte pull, so this skips instead:
/// </para>
/// <code>
/// $env:OPENROUTER_API_KEY = "sk-or-..."
/// dotnet test tests/Rag.NET.Parsers.Vision.IntegrationTests
/// </code>
/// </remarks>
public sealed class RealVisionModelTests
{
    private const string SkipReason =
        "Set OPENROUTER_API_KEY to run the real vision model. Roughly $0.0004 per run across two " +
        "calls. There is no local fallback: TestChatClientFactory.Create's Ollama fallback is a " +
        "text model, and a local vision model is a multi-gigabyte pull.";

    private static string ImagePath =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "sample-image.png");

    private static DocumentMetadata Metadata() => new()
    {
        DocumentId = new DocumentId("image-1"),
        FileName = "sample-image.png",
        ContentType = "image/png",
    };

    [Fact]
    public void TheFixtureShipsBesideTheTest()
    {
        // Guards the suite: a missing fixture would fail the model tests for a reason unrelated to
        // the model, after paying for the calls.
        Assert.True(File.Exists(ImagePath), $"sample-image.png was not copied to {ImagePath}.");
    }

    /// <remarks>
    /// Asserts on <b>content</b>, and on two kinds of it. Reading the words back is optical
    /// character recognition; naming the red circle is not. A model that returned a fluent
    /// description of some other image would satisfy a non-emptiness check and fail both of these.
    /// </remarks>
    [Fact]
    public async Task TheRealModel_ReadsTheTextAndSeesTheShape()
    {
        Assert.SkipUnless(TestChatClientFactory.IsOpenRouterAvailable, SkipReason);

        using var chatClient = TestChatClientFactory.CreateVisionClient();
        var sut = new ImageDocumentParser(chatClient, new ImageDescriptionOptions());

        await using var image = File.OpenRead(ImagePath);
        var sections = await sut
            .ParseAsync(image, Metadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var section = Assert.Single(sections);
        Assert.False(
            string.IsNullOrWhiteSpace(section.Text),
            $"{TestChatClientFactory.VisionModelId} returned an empty description. If this model " +
            "has been delisted the failure is usually an HTTP 404 rather than this, but a model " +
            "that stopped accepting images would look exactly like this.");

        // The text, verbatim. Both words, because "test" alone appears in almost any description of
        // a file named sample-image.png in a test suite.
        Assert.Contains("INTEGRATION", section.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TEST", section.Text, StringComparison.OrdinalIgnoreCase);

        // The non-textual half. Kept tolerant on wording — "red circle", "red dot" and "circle in
        // red" are all correct answers, and pinning one phrasing would test the model's prose style
        // rather than its sight.
        var described = section.Text;
        Assert.Contains("red", described, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            described.Contains("circle", StringComparison.OrdinalIgnoreCase) ||
            described.Contains("dot", StringComparison.OrdinalIgnoreCase) ||
            described.Contains("sphere", StringComparison.OrdinalIgnoreCase) ||
            described.Contains("round", StringComparison.OrdinalIgnoreCase),
            $"{TestChatClientFactory.VisionModelId} read the text but named no round shape, which " +
            $"suggests it received the prompt without the image. Description: {described}");

        Assert.Equal("image_description", section.Heading);
        Assert.Equal("image-1", section.DocumentId.Value);
    }

    /// <remarks>
    /// <para>
    /// The prompt is a documented extension point — <c>ImageDescriptionOptions.Prompt</c>, with a
    /// <c>{fileName}</c> placeholder the parser substitutes. A substitute proves the string is
    /// passed along; it cannot show that a model reads and obeys it, which is the only reason the
    /// option is worth having.
    /// </para>
    /// <para>
    /// Asks for a single word so the assertion is about compliance rather than about prose: a parser
    /// that ignored the option would return the default's full paragraph, which is not one word.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheCustomPromptReachesTheModelAndChangesTheAnswer()
    {
        Assert.SkipUnless(TestChatClientFactory.IsOpenRouterAvailable, SkipReason);

        using var chatClient = TestChatClientFactory.CreateVisionClient();
        var sut = new ImageDocumentParser(chatClient, new ImageDescriptionOptions
        {
            Prompt = "Reply with only the single colour of the circle in {fileName}. One word.",
        });

        await using var image = File.OpenRead(ImagePath);
        var sections = await sut
            .ParseAsync(image, Metadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var section = Assert.Single(sections);

        Assert.Contains("red", section.Text, StringComparison.OrdinalIgnoreCase);

        // Not a style check: the default prompt asks for a full description, so a long answer here
        // is the specific evidence that the custom prompt never arrived. Generous bound, because
        // some models prepend a courtesy sentence to a one-word answer and that is still obedience.
        Assert.True(
            section.Text.Length < 200,
            $"Asked for one word and got {section.Text.Length} characters, which is what the " +
            $"DEFAULT prompt produces. The custom prompt likely never reached the model. " +
            $"Answer: {section.Text}");
    }
}
