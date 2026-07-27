using Rag.NET.Parsers.Pdf.Ocr;

namespace Rag.NET.Parsers.Pdf.Tests;

/// <summary>
/// Hand-written fake for the document-level OCR seam (this project uses no mocking library).
/// Records the exact number of <see cref="RecognizeAsync"/> calls and the byte length of each
/// PDF handed over, so the cost-critical tests can assert on numbers: "the engine was never
/// called" and "the engine was called once, not once per page" must never be satisfiable by
/// a test that merely did not throw.
/// </summary>
internal sealed class FakeDocumentOcrEngine : IDocumentOcrEngine
{
    private readonly Func<byte[], CancellationToken, DocumentOcrResult> _behavior;
    private int _calls;

    private FakeDocumentOcrEngine(Func<byte[], CancellationToken, DocumentOcrResult> behavior) =>
        _behavior = behavior;

    /// <summary>Number of engine invocations. One per document that needed OCR, or zero.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Byte length of each PDF handed to the engine, in call order.</summary>
    public List<int> ReceivedPdfLengths { get; } = [];

    public static FakeDocumentOcrEngine Returning(IReadOnlyDictionary<int, string> pageText) =>
        new((_, _) => new DocumentOcrResult(pageText, pageText.Count));

    public static FakeDocumentOcrEngine Empty() => Returning(new Dictionary<int, string>());

    public static FakeDocumentOcrEngine Throwing(Exception exception) =>
        new((_, _) => throw exception);

    /// <summary>
    /// Cancels <paramref name="source"/> and then throws through the token the parser handed
    /// over. If the parser did not forward the caller's token, nothing is cancelled and the
    /// distinctive <see cref="InvalidOperationException"/> below fails the test instead.
    /// </summary>
    public static FakeDocumentOcrEngine CancellingVia(CancellationTokenSource source) =>
        new((_, token) =>
        {
            source.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The parser did not forward the caller's cancellation token.");
        });

    public async ValueTask<DocumentOcrResult> RecognizeAsync(Stream pdf, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        using var buffer = new MemoryStream();
        await pdf.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        ReceivedPdfLengths.Add((int)buffer.Length);
        return _behavior(buffer.ToArray(), cancellationToken);
    }
}
