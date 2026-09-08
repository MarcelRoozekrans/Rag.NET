using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Vision.Tests;

/// <summary>
/// A failing vision model must surface as <see cref="VisionDescriptionException"/> rather than as
/// whatever type the provider's SDK happened to throw (#497).
/// </summary>
/// <remarks>
/// <para>
/// <b>These run without a network, which is the point.</b> The defect was found by a live
/// integration test that fails roughly one run in three — twice in one afternoon, with two
/// different causes — and a bug reproducible only against a third-party service at a rate nobody
/// controls is a bug that gets re-diagnosed every time it appears. The two shapes below are the two
/// that were actually observed against OpenRouter, reproduced from a substitute.
/// </para>
/// <para>
/// <b>The wrapped type is the assertion, not the message.</b> A caller cannot act on
/// <c>ArgumentOutOfRangeException</c> from the OpenAI SDK's finish-reason validator: it is thrown by
/// a transitive dependency, it is indistinguishable from a genuine argument bug in the caller's own
/// code, and it appears in no contract this library publishes.
/// </para>
/// </remarks>
public class VisionProviderFailureTests
{
    private static readonly DocumentMetadata PngMetadata = new()
    {
        DocumentId = new DocumentId("img.png"),
        FileName = "img.png",
        ContentType = "image/png",
    };

    /// <summary>Vision only — OCR would short-circuit the call under test.</summary>
    private static ImageDescriptionOptions VisionOnly() => new() { TryOcrBeforeVision = false };

    private static async Task<Exception> ParseAndCatchAsync(Exception thrownByProvider)
    {
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrownByProvider);

        var sut = new ImageDocumentParser(client, VisionOnly());

        using var stream = new MemoryStream([1, 2, 3, 4]);

        return await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in sut.ParseAsync(stream, PngMetadata, TestContext.Current.CancellationToken))
            {
                // Draining the sequence is what forces the call; nothing is asserted per section.
            }
        }) ?? throw new InvalidOperationException("The parse was expected to throw and did not.");
    }

    /// <summary>
    /// The shape #497 was filed for: OpenRouter answers <c>finish_reason: "error"</c> and the OpenAI
    /// SDK's enum validator throws before any response reaches this library.
    /// </summary>
    [Fact]
    public async Task AnUnknownFinishReason_SurfacesAsAVisionDescriptionException()
    {
        // MA0015 wants a paramName matching a parameter of this method. This exception is a
        // verbatim reproduction of one thrown inside the OpenAI SDK, where "value" is the
        // parameter that validator rejects — changing it would make the fixture describe a
        // different failure than the one observed.
#pragma warning disable MA0015
        var sdkFailure = new ArgumentOutOfRangeException(
            "value", "error", "Unknown ChatFinishReason value.");
#pragma warning restore MA0015

        var caught = await ParseAndCatchAsync(sdkFailure);

        var vision = Assert.IsType<VisionDescriptionException>(caught);
        Assert.Same(sdkFailure, vision.InnerException);
        Assert.Equal("img.png", vision.FileName);
        Assert.Contains("img.png", vision.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second observed shape: the provider rate-limits. Distinct cause, same consequence for a
    /// caller — no description was produced.
    /// </summary>
    [Fact]
    public async Task AProviderError_SurfacesAsAVisionDescriptionException()
    {
        var rateLimited = new InvalidOperationException("HTTP 429 (: ) Provider returned error");

        var caught = await ParseAndCatchAsync(rateLimited);

        var vision = Assert.IsType<VisionDescriptionException>(caught);
        Assert.Same(rateLimited, vision.InnerException);
    }

    /// <summary>
    /// Cancellation is the caller's own, and must not be reclassified as a provider failure — the
    /// convention <c>RagError.TransportFailed</c> already documents.
    /// </summary>
    [Fact]
    public async Task Cancellation_PropagatesRatherThanBeingWrapped()
    {
        var caught = await ParseAndCatchAsync(new OperationCanceledException());

        Assert.IsNotType<VisionDescriptionException>(caught);
        Assert.IsAssignableFrom<OperationCanceledException>(caught);
    }

    /// <summary>
    /// A blown budget is this library's own stop signal, raised by a decorator around the client,
    /// and must not be reclassified as a provider failure.
    /// </summary>
    /// <remarks>
    /// Wrapping it would invite exactly the response a spend limit exists to prevent. The same
    /// hazard is why <c>FallbackChatClient.IsTransient</c> pins <c>BudgetExceededException</c> by
    /// type before any other test: a transient classification there would retry against the next
    /// provider and keep spending past the limit.
    /// </remarks>
    [Fact]
    public async Task ABlownBudget_PropagatesRatherThanBeingWrapped()
    {
        var budget = new BudgetExceededException(CostWindow.Day, limit: 1.00m, spend: 1.25m);

        var caught = await ParseAndCatchAsync(budget);

        Assert.IsNotType<VisionDescriptionException>(caught);
        Assert.Same(budget, caught);
    }

    /// <summary>
    /// The wrapping must not have changed the working path: a provider that answers still yields
    /// its description. Without this, a mutation that threw unconditionally would satisfy every
    /// assertion above.
    /// </summary>
    [Fact]
    public async Task AWorkingProvider_StillYieldsItsDescription()
    {
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "a red square")));

        var sut = new ImageDocumentParser(client, VisionOnly());

        using var stream = new MemoryStream([1, 2, 3, 4]);

        var sections = new List<DocumentSection>();
        await foreach (var section in sut.ParseAsync(stream, PngMetadata, TestContext.Current.CancellationToken))
            sections.Add(section);

        var only = Assert.Single(sections);
        Assert.Equal("a red square", only.Text);
    }
}
