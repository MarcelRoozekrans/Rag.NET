namespace Rag.NET.DataProviders;

/// <summary>
/// A read-only wrapper over an entry's content stream that remembers the first exception a read
/// threw, and rethrows it unchanged. Owns and disposes the stream it wraps.
/// </summary>
/// <remarks>
/// Without a hash store the content is read by the parser inside <c>IngestAsync</c>, so a
/// cancellation the caller did not ask for reaches the per-entry catch with nothing saying
/// whether the content read stalled or a store timed out (#1019). The catch asks
/// <see cref="IsFault"/> instead. It records the exception instance rather than a "some read
/// failed" flag, so a read error the parser handled cannot relabel a later store timeout.
/// Seek and length calls are not recorded: they are not a stalled download.
/// </remarks>
internal sealed class FaultRecordingStream(Stream inner) : Stream
{
    private Exception? _fault;

    /// <summary>The first exception a read threw, or <see langword="null"/>.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>
    /// Whether <paramref name="ex"/> is the recorded fault, or carries it in its inner-exception
    /// chain because a parser wrapped it.
    /// </summary>
    public bool IsFault(Exception ex)
    {
        var fault = Fault;
        if (fault is null)
            return false;

        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (ReferenceEquals(current, fault))
                return true;
        }

        return false;
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return inner.Read(buffer, offset, count); }
        catch (Exception ex) { Record(ex); throw; }
    }

    public override int Read(Span<byte> buffer)
    {
        try { return inner.Read(buffer); }
        catch (Exception ex) { Record(ex); throw; }
    }

    public override int ReadByte()
    {
        try { return inner.ReadByte(); }
        catch (Exception ex) { Record(ex); throw; }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try { return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { Record(ex); throw; }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { Record(ex); throw; }
    }

    public override void CopyTo(Stream destination, int bufferSize)
    {
        try { inner.CopyTo(destination, bufferSize); }
        catch (Exception ex) { Record(ex); throw; }
    }

    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        try { await inner.CopyToAsync(destination, bufferSize, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { Record(ex); throw; }
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
        // Read-only: nothing to flush.
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void Record(Exception ex) => Interlocked.CompareExchange(ref _fault, ex, null);
}
