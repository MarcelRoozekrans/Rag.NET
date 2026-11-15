using System.Net;
using Rag.NET.DataProviders.Http;
using Xunit;

namespace Rag.NET.DataProviders.Airtable.Tests;

public sealed class TimeoutBoundedResponseStreamTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeoutBoundedResponseStream Wrap(Stream inner, TimeSpan timeout, TrackedResponse? response = null) =>
        new(response ?? new TrackedResponse(HttpStatusCode.OK), inner, timeout);

    private static void AssertTimedOut(TaskCanceledException ex)
    {
        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Contains("HttpClient.Timeout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStalledReadAsync_TimesOutAsHttpClientDoes()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using var sut = Wrap(new ScriptedStream(), Short);

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            _ = await sut.ReadAsync(new byte[8].AsMemory(), caller.Token));

        AssertTimedOut(ex);
        Assert.False(caller.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task AStalledReadAsync_ByteArrayOverload_TimesOutAsHttpClientDoes()
    {
        await using var sut = Wrap(new ScriptedStream(), Short);

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            _ = await sut.ReadAsync(new byte[8], 0, 8, Token));

        AssertTimedOut(ex);
    }

    [Fact]
    public void AStalledSyncRead_TimesOutAsHttpClientDoes()
    {
        using var sut = Wrap(new ScriptedStream(), Short);
        var buffer = new byte[8];

        var ex = Assert.Throws<TaskCanceledException>(() => sut.Read(buffer, 0, 8));

        AssertTimedOut(ex);
    }

    [Fact]
    public void AStalledSyncSpanRead_TimesOutAsHttpClientDoes()
    {
        using var sut = Wrap(new ScriptedStream(), Short);
        var buffer = new byte[8];

        var ex = Assert.Throws<TaskCanceledException>(() => sut.Read(buffer.AsSpan()));

        AssertTimedOut(ex);
    }

    [Fact]
    public async Task SlowSteadyReads_CompleteBeyondTheTimeout()
    {
        var inner = new ScriptedStream(delay: TimeSpan.FromMilliseconds(100), bytes: 5);
        await using var sut = Wrap(inner, TimeSpan.FromMilliseconds(300));
        using var copy = new MemoryStream();

        await sut.CopyToAsync(copy, Token);

        Assert.Equal(5, copy.Length);
    }

    [Fact]
    public async Task CopyToAsync_IsBoundedToo()
    {
        var inner = new ScriptedStream(delay: TimeSpan.Zero, bytes: 2, stallAfter: true);
        await using var sut = Wrap(inner, Short);
        using var copy = new MemoryStream();

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => sut.CopyToAsync(copy, Token));

        AssertTimedOut(ex);
        Assert.Equal(2, copy.Length);
    }

    [Fact]
    public async Task TheCallersCancellation_IsNotReportedAsATimeout()
    {
        await using var sut = Wrap(new ScriptedStream(), TimeSpan.FromSeconds(10));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        caller.CancelAfter(TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            _ = await sut.ReadAsync(new byte[8].AsMemory(), caller.Token));

        Assert.Equal(caller.Token, ex.CancellationToken);
        Assert.IsNotType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task AnInfiniteTimeout_NeverCutsOff()
    {
        var inner = new ScriptedStream(delay: TimeSpan.FromMilliseconds(300), bytes: 1);
        await using var sut = Wrap(inner, Timeout.InfiniteTimeSpan);

        var read = await sut.ReadAsync(new byte[8].AsMemory(), Token);

        Assert.Equal(1, read);
    }

    [Fact]
    public void Dispose_DisposesTheResponse()
    {
        var response = new TrackedResponse(HttpStatusCode.OK);
        var sut = Wrap(new ScriptedStream(), Short, response);

        sut.Dispose();

        Assert.True(response.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheResponse()
    {
        var response = new TrackedResponse(HttpStatusCode.OK);
        var sut = Wrap(new ScriptedStream(), Short, response);

        await sut.DisposeAsync();

        Assert.True(response.Disposed);
    }

    [Fact]
    public async Task OpenAsync_ANonSuccessStatus_ThrowsAndDisposesTheResponse()
    {
        var response = new TrackedResponse(HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            TimeoutBoundedResponseStream.OpenAsync(response, Short, Token));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.True(response.Disposed);
    }

    [Fact]
    public async Task OpenAsync_Success_ReturnsTheBody()
    {
        var response = new TrackedResponse(HttpStatusCode.OK) { Content = new StringContent("abc") };

        await using var stream = await TimeoutBoundedResponseStream.OpenAsync(response, Short, Token);
        using var reader = new StreamReader(stream);

        Assert.Equal("abc", await reader.ReadToEndAsync(Token));
    }

    private sealed class TrackedResponse(HttpStatusCode status) : HttpResponseMessage(status)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Yields <c>bytes</c> single bytes, each after <c>delay</c>; then ends, or stalls until the
    /// read is cancelled. With no bytes it stalls at once.
    /// </summary>
    private sealed class ScriptedStream(TimeSpan delay = default, int bytes = 0, bool stallAfter = false) : Stream
    {
        private int _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent >= bytes)
            {
                if (stallAfter || bytes == 0)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                return 0;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            _sent++;
            buffer.Span[0] = 1;
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
