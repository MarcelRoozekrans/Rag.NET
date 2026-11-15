
namespace Rag.NET.DataProviders.Http;

/// <summary>
/// A response body stream whose every read is bounded by <c>timeout</c>, normally
/// <see cref="HttpClient.Timeout"/> (#1012).
/// </summary>
/// <remarks>
/// A connector that hands out a live body, read with <see cref="HttpCompletionOption.ResponseHeadersRead"/>,
/// is no longer covered by <see cref="HttpClient.Timeout"/>: that stops at the headers, so a server
/// that stalls mid-body would hold the read until the caller's token fired. The deadline is per
/// read and idle, not a total over the whole body. A total would cut off a large attachment on a
/// slow link that is still making progress. Its expiry is thrown like <see cref="HttpClient"/>'s
/// own timeout: a <see cref="TaskCanceledException"/> around a <see cref="TimeoutException"/>,
/// with the caller's token not cancelled. The caller's own cancellation stays the caller's
/// <see cref="OperationCanceledException"/>. Synchronous reads are bounded too, because the
/// ZIP, OpenXml and MSG parsers read synchronously. Disposing the stream disposes the response.
/// <para>
/// This file lives in <c>src/Shared</c> and is linked into each connector project, as
/// <c>GraphErrorMapping.cs</c> is, so no package gains a public type or a dependency.
/// </para>
/// </remarks>
internal sealed class TimeoutBoundedResponseStream : Stream
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _content;
    private readonly TimeSpan _timeout;

    internal TimeoutBoundedResponseStream(HttpResponseMessage response, Stream content, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(content);
        _response = response;
        _content = content;
        _timeout = timeout;
    }

    /// <summary>
    /// Checks the status, then wraps the body. Disposes <paramref name="response"/> if either
    /// step throws, so a failed open leaks no connection.
    /// </summary>
    internal static async Task<Stream> OpenAsync(
        HttpResponseMessage response, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            _ = response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new TimeoutBoundedResponseStream(response, content, timeout);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_timeout != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(_timeout);
        }

        try
        {
            return await _content.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"The request was canceled due to the configured HttpClient.Timeout of {_timeout.TotalSeconds} seconds elapsing.",
                new TimeoutException(ex.Message, ex));
        }
        catch (OperationCanceledException ex)
            when (cancellationToken.IsCancellationRequested && ex.CancellationToken != cancellationToken)
        {
            // The inner read saw the linked token; the caller is owed its own.
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(Span<byte> buffer)
    {
        // The inner read cannot take a Span across an await, so it goes through a rented copy.
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var read = ReadAsync(rented.AsMemory(0, buffer.Length), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

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
            _content.Dispose();
            _response.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _content.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
