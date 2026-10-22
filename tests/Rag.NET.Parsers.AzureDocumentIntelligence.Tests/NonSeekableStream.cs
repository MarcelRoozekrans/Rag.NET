namespace Rag.NET.Parsers.AzureDocumentIntelligence.Tests;

/// <summary>
/// A forward-only stream, like an HTTP response body or a pipe: it cannot seek and has no
/// <see cref="Length"/>, so the parser cannot learn the document's size without reading it. It
/// counts what was read, so a test can prove the size cap stopped reading instead of buffering
/// the whole document first.
/// </summary>
internal sealed class NonSeekableStream(byte[] content) : Stream
{
    private readonly MemoryStream _inner = new(content, writable: false);

    /// <summary>The total number of bytes handed out by reads so far.</summary>
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Count(int read)
    {
        BytesRead += read;
        return read;
    }
}
