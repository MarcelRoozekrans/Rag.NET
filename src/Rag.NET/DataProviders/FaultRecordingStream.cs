namespace Rag.NET.DataProviders;

/// <summary>
/// A read-only wrapper over an entry's content stream that remembers every exception a read
/// threw, and rethrows each unchanged. Owns and disposes the stream it wraps.
/// </summary>
/// <remarks>
/// Without a hash store the content is read by the parser inside <c>IngestAsync</c>, so a
/// cancellation the caller did not ask for reaches the per-entry catch with nothing saying
/// whether the content read stalled or a store timed out (#1019). The catch asks
/// <see cref="IsFault"/> instead. It records the exception instance rather than a "some read
/// failed" flag, so a read error the parser handled cannot relabel a later store timeout. Every
/// fault is kept, not just one: a parser may swallow one and let a later one propagate, or save a
/// timeout, try a fallback read that fails too, and rethrow the first.
/// Seek and length calls are not recorded: they are not a stalled download. Neither are copies
/// as a whole: <see cref="Stream.CopyTo(Stream, int)"/> and its async form are inherited, so they
/// read through this stream's own read methods and a failing write to the destination is not
/// mistaken for a read fault.
/// </remarks>
internal sealed class FaultRecordingStream(Stream inner) : Stream
{
    private readonly List<Exception> _faults = [];

    /// <summary>Every exception a read threw, oldest first.</summary>
    public IReadOnlyList<Exception> Faults
    {
        get { lock (_faults) return [.. _faults]; }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is a recorded fault, or carries one in its inner-exception
    /// chain because a parser wrapped it.
    /// </summary>
    public bool IsFault(Exception ex)
    {
        var faults = Faults;
        for (var current = ex; current is not null; current = current.InnerException)
        {
            foreach (var fault in faults)
            {
                if (ReferenceEquals(current, fault))
                    return true;
            }
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

    // Not base.DisposeAsync: that calls Dispose(true), which would dispose the inner stream a
    // second time, synchronously.
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void Record(Exception ex)
    {
        lock (_faults)
            _faults.Add(ex);
    }
}
