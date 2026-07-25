using Rag.NET.Parsers.Pdf.Ocr;

namespace Rag.NET.Parsers.Pdf.Tests;

/// <summary>
/// Fake OCR engine injected through the internal seam. Records the call count and — for the
/// concurrency smoke test — flags overlapping (non-serialized) <see cref="Recognize"/> calls
/// via an <see cref="Interlocked"/>-maintained active counter.
/// </summary>
internal sealed class FakePdfOcrEngine : IPdfOcrEngine
{
    private readonly Func<byte[], string?> _behavior;
    private int _calls;
    private int _active;
    private int _overlapped;

    public FakePdfOcrEngine(string? result) => _behavior = _ => result;

    public FakePdfOcrEngine(Exception exception) => _behavior = _ => throw exception;

    public FakePdfOcrEngine(Func<byte[], string?> behavior) => _behavior = behavior;

    public int Calls => Volatile.Read(ref _calls);

    public bool SawOverlap => Volatile.Read(ref _overlapped) == 1;

    /// <summary>Byte length of each image handed to <see cref="Recognize"/>, in call order.</summary>
    public List<int> ByteLengths { get; } = [];

    public string? Recognize(byte[] imageBytes)
    {
        if (Interlocked.Increment(ref _active) > 1)
        {
            Interlocked.Exchange(ref _overlapped, 1);
        }

        try
        {
            Interlocked.Increment(ref _calls);
            ByteLengths.Add(imageBytes.Length);
            // Widen the race window so unserialized parallel calls reliably overlap.
            Thread.Sleep(10);
            return _behavior(imageBytes);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}
