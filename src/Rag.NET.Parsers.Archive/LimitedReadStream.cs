namespace Rag.NET.Parsers.Archive;

/// <summary>
/// Wraps one archive entry's decompression stream and counts the bytes it <b>actually produces</b>,
/// refusing the archive with an <see cref="ArchiveLimitExceededException"/> the moment a bound is
/// passed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here trusts a declared size, and that is the point of the type.</b>
/// <c>ZipArchiveEntry.Length</c> is read from the archive's central directory, which is written by
/// whoever built the archive — an attacker declares whatever size flatters the file. A parser that
/// pre-flighted <c>Length</c> would admit a 40 KB archive claiming to hold 40 KB and then decompress
/// gigabytes out of it, having already passed its only check. Counting what a read returns is the
/// only measurement the archive cannot author.
/// </para>
/// <para>
/// <b>The ratio's denominator is attacker-controlled too, and the lie fails safe.</b>
/// <c>CompressedLength</c> comes from the same central directory. Understating it makes the computed
/// ratio <i>higher</i>, so the bound trips <i>earlier</i> — the archive is refused sooner than it
/// deserved, which costs an attacker their bomb and costs an honest archive nothing it can reach.
/// Overstating it is bounded by physics rather than by trust: the compressed bytes have to exist in
/// the stream the caller already holds, so an entry cannot claim to be larger than the file it
/// arrived in. A zero or negative claim is read as <c>1</c>, which makes any output at all look like
/// a bomb-grade expansion — the safe reading of a nonsensical header.
/// </para>
/// <para>
/// <b>Order of refusal.</b> When one read breaches both bounds, the ratio is reported. It is the more
/// specific diagnosis — it names an entry as malicious, where the total only says the archive got too
/// big — and reporting the vaguer one would tell an operator the wrong thing about the same file.
/// </para>
/// <para>
/// This is a read-only, forward-only stream: an entry's deflate stream is not seekable and does not
/// know its own length, so <see cref="Length"/>, <see cref="Position"/> and <see cref="Seek"/> throw
/// rather than lie. Nothing is caught here — an
/// <see cref="OperationCanceledException"/> from the underlying stream is the caller's decision about
/// the whole parse and is not this type's to reinterpret as a bomb.
/// </para>
/// </remarks>
internal sealed class LimitedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _compressedLength;
    private readonly ArchiveReadBudget _budget;
    private long _entryUncompressedBytes;

    /// <summary>Wraps an entry's decompression stream.</summary>
    /// <param name="inner">The entry stream to read through, disposed with this one.</param>
    /// <param name="compressedLength">
    /// The entry's declared compressed size, the ratio's denominator. Untrusted; see the type's
    /// remarks for why the untrustworthiness is safe in this direction.
    /// </param>
    /// <param name="budget">The archive-wide running total. One instance across every entry.</param>
    public LimitedReadStream(Stream inner, long compressedLength, ArchiveReadBudget budget)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(budget);

        _inner = inner;
        _compressedLength = compressedLength;
        _budget = budget;
    }

    /// <summary>Gets the bytes this entry has produced so far.</summary>
    public long EntryUncompressedBytes => _entryUncompressedBytes;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException(
        "An archive entry's decompressed length is not known ahead of reading it, and the length the "
        + "archive declares is written by whoever built the archive.");

    public override long Position
    {
        get => throw new NotSupportedException("An archive entry's decompression stream is forward-only.");
        set => throw new NotSupportedException("An archive entry's decompression stream is forward-only.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Account(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Flush()
    {
        // Nothing is buffered here, and a read-only stream has nothing to flush. Not a
        // NotSupportedException: Stream.CopyTo and several BCL helpers call Flush unconditionally.
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("An archive entry's decompression stream is forward-only.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("An archive entry's decompression stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("An archive entry's decompression stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Books the bytes a read returned against both bounds, and refuses the archive if either has
    /// been passed.
    /// </summary>
    /// <param name="read">What the underlying read returned. Zero means the entry is exhausted.</param>
    /// <remarks>
    /// The bounds are checked <i>after</i> the bytes are counted rather than predicted before the
    /// read, because predicting means trusting the requested count instead of the produced one — the
    /// same mistake as trusting the declared length, one layer down.
    /// </remarks>
    private void Account(int read)
    {
        if (read <= 0)
        {
            return;
        }

        _entryUncompressedBytes += read;
        _budget.Add(read);

        // Ratio first: see the type's remarks on the order of refusal.
        var denominator = _compressedLength > 0 ? _compressedLength : 1;
        if (_entryUncompressedBytes > denominator * _budget.MaxCompressionRatio)
        {
            throw new ArchiveLimitExceededException(
                ArchiveLimit.CompressionRatio, _entryUncompressedBytes / denominator, _budget.MaxCompressionRatio);
        }

        _budget.ThrowIfTotalExceeded();
    }
}
