using Rag.NET.Parsers.Pdf.Ocr;

namespace Rag.NET.Parsers.Pdf.Tests;

/// <summary>
/// Fake OCR engine injected through the internal seam. Records the call count and — for the
/// concurrency smoke test — flags overlapping (non-serialized) <see cref="Recognize"/> calls
/// via an <see cref="Interlocked"/>-maintained active counter.
/// </summary>
internal sealed class FakePdfOcrEngine : IPdfOcrEngine
{
    private readonly string? _result;
    private readonly Exception? _exception;
    private int _calls;
    private int _active;
    private int _overlapped;

    public FakePdfOcrEngine(string? result) => _result = result;

    public FakePdfOcrEngine(Exception exception) => _exception = exception;

    public int Calls => Volatile.Read(ref _calls);

    public bool SawOverlap => Volatile.Read(ref _overlapped) == 1;

    public string? Recognize(byte[] imageBytes)
    {
        if (Interlocked.Increment(ref _active) > 1)
        {
            Interlocked.Exchange(ref _overlapped, 1);
        }

        try
        {
            Interlocked.Increment(ref _calls);
            // Widen the race window so unserialized parallel calls reliably overlap.
            Thread.Sleep(10);
            return _exception is null ? _result : throw _exception;
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}
